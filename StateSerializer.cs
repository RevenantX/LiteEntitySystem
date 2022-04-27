using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib.Utils;
using EntityClassData = LiteEntitySystem.EntityManager.EntityClassData;
using InternalEntity = LiteEntitySystem.EntityManager.InternalEntity;

namespace LiteEntitySystem
{
    internal sealed class StateSerializer
    {
        private byte _version;

        private const int MaxHistory = 32; //should be power of two
        public const int HeaderSize = 5;
        public const int HeaderWithTotalSize = 7;
        public const int DiffHeaderSize = 4;

        private EntityClassData _classData;
        private InternalEntity _entityLogic;
        private readonly NetDataWriter[] _history = new NetDataWriter[MaxHistory];
        private byte[] _latestEntityData;
        private ushort[] _fieldChangeTicks;
        private ushort _versionChangedTick;

        private class RemoteCallPacket
        {
            public byte Id;
            public ushort Tick;
            public ushort LifeTime;
            public byte[] Data;
            public RemoteCallPacket Next;
            public RemoteCallPacket Prev;
        }
        
        private byte[] _packets;
        private int _packetsCount;
        private ushort _lastWriteTick;

        private RemoteCallPacket _rpcHead;
        private RemoteCallPacket _rpcTail;
        private Queue<RemoteCallPacket> _rpcPool;

        public void AddRemoteCall<T>(ushort tick, T value, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = new RemoteCallPacket
            {
                Id = remoteCallInfo.Id,
                Tick = tick,
                Data = new byte[remoteCallInfo.DataSize],
                LifeTime = remoteCallInfo.LifeTime
            };
            unsafe
            {
                fixed (byte* rawData = rpc.Data)
                {
                    Unsafe.Copy(rawData, ref value);
                }
            }

            if (_rpcHead == null)
            {
                _rpcHead = rpc;
            }
            else
            {
                _rpcTail.Next = rpc;
                rpc.Prev = _rpcTail;
            }
            _rpcTail = rpc;
        }

        public byte IncrementVersion(ushort tick)
        {
            _lastWriteTick = (ushort)(tick - 1);
            _versionChangedTick = tick;
            return _version++;
        }

        internal void Init(EntityClassData classData, InternalEntity e)
        {
            _classData = classData;
            _entityLogic = e;
            if (classData.IsServerOnly)
                return;

            int minimalDataSize = HeaderSize + _classData.FieldsFlagsSize + _classData.FixedFieldsSize;
            Utils.ResizeOrCreate(ref _latestEntityData, minimalDataSize);
            Utils.ResizeOrCreate(ref _fieldChangeTicks, classData.FieldsCount);

            unsafe
            {
                fixed (byte* data = _latestEntityData)
                {
                    *(ushort*) (data) = e.Id;
                    data[2] = e.Version;
                    *(ushort*) (data + 3) = e.ClassId;
                }
            }

            //only if revertable
            if(_history[0] == null)
                for (int i = 0; i < MaxHistory; i++)
                    _history[i] = new NetDataWriter(true, HeaderSize); 
        }

        public unsafe void WritePredicted(int latestDataOffset, byte* data, uint size)
        {
            fixed (byte* latestEntityData = _latestEntityData)
            {
                Unsafe.CopyBlock(latestEntityData + HeaderSize + latestDataOffset, data, size);
            }
        }

        private unsafe void Write(ushort serverTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick) 
                return;
            
            _lastWriteTick = serverTick;

            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);
            int offset = HeaderSize;

            fixed (byte* latestEntityData = _latestEntityData)
            {
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var entityFieldInfo = ref _classData.Fields[i];
                    byte* fieldPtr = entityPointer + entityFieldInfo.Offset;
                    byte* latestDataPtr = latestEntityData + offset;

                    //update only changed fields
                    switch (entityFieldInfo.Type)
                    {
                        case FixedFieldType.None:
                            if (Utils.memcmp(latestDataPtr, fieldPtr, entityFieldInfo.PtrSize) != 0)
                            {
                                Unsafe.CopyBlock(latestDataPtr, fieldPtr, entityFieldInfo.Size);
                                _fieldChangeTicks[i] = serverTick;
                            }
                            offset += entityFieldInfo.IntSize;
                            break;
                        
                        case FixedFieldType.EntityId:
                            ushort entityId = Unsafe.AsRef<EntityLogic>(fieldPtr)?.Id ?? EntityManager.InvalidEntityId;
                            ushort *ushortPtr = (ushort*)latestDataPtr;
                            if (*ushortPtr != entityId)
                            {
                                *ushortPtr = entityId;
                                _fieldChangeTicks[i] = serverTick;
                            }
                            offset += 2;
                            break;
                    }
                }
            }
        }

        public unsafe int MakeBaseline(ushort serverTick, NetDataWriter result)
        {
            if (_classData.IsServerOnly)
                return -1;
            Write(serverTick);
            
            //make diff
            int startPos = result.Length;
            int lastDataOffset = HeaderSize;

            fixed (byte* lastEntityData = _latestEntityData, resultData = result.Data)
            {
                //initial state with compression
                //dont write total size in full sync and fields
                //totalSizePos here equal to EID position
                //set fields to sync all
                Unsafe.CopyBlock(resultData + startPos, lastEntityData, HeaderSize);
                int resultOffset = startPos + HeaderSize;
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var fixedFieldInfo = ref _classData.Fields[i];
                    Unsafe.CopyBlock(
                        resultData + resultOffset, 
                        lastEntityData + lastDataOffset,
                        fixedFieldInfo.Size);
                    resultOffset += fixedFieldInfo.IntSize;
                    lastDataOffset += fixedFieldInfo.IntSize;
                }

                byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset).FullSyncWrite(resultData, ref resultOffset);
                }
                result.SetPosition(resultOffset);
                return resultOffset;
            }
        }

        public unsafe int MakeDiff(ushort minimalTick, ushort serverTick, ushort playerTick, NetDataWriter result)
        {
            if (_classData.IsServerOnly)
                return -1;
            Write(serverTick);

            //remove old RPCs
            while (_rpcHead != null && EntityManager.SequenceDiff(minimalTick, _rpcHead.Tick) >= 0)
            {
                _rpcPool.Enqueue(_rpcHead);
                if (_rpcTail == _rpcHead)
                    _rpcTail = null;
                _rpcHead = _rpcHead.Next;
            }

            //make diff
            int startPos = result.Length;
            int resultOffset;
            int lastDataOffset = HeaderSize;
            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);

            fixed (byte* lastEntityData = _latestEntityData, resultData = result.Data)
            {
                ushort* fieldFlagsPtr = (ushort*) (resultData + startPos);
                //initial state with compression
                if (EntityManager.SequenceDiff(_versionChangedTick, playerTick) > 0)
                {
                    //write full header here (totalSize + eid)
                    //also all fields
                    *fieldFlagsPtr = 1;
                    Unsafe.CopyBlock(resultData + startPos + sizeof(ushort), lastEntityData, HeaderSize);
                    resultOffset = startPos + HeaderWithTotalSize;
                    for (int i = 0; i < _classData.FieldsCount; i++)
                    {
                        ref var fixedFieldInfo = ref _classData.Fields[i];
                        Unsafe.CopyBlock(
                            resultData + resultOffset, 
                            lastEntityData + lastDataOffset,
                            fixedFieldInfo.Size);
                        resultOffset += fixedFieldInfo.IntSize;
                        lastDataOffset += fixedFieldInfo.IntSize;
                    }
                    for (int i = 0; i < _classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset).FullSyncWrite(resultData, ref resultOffset);
                    }
                }
                else //make diff
                {
                    bool hasChanges = false;
                    // -1 for cycle
                    byte* fields = resultData + startPos + DiffHeaderSize - 1;
                    *fieldFlagsPtr = 0;
                    //put entity id
                    *(ushort*)(resultData + startPos + sizeof(ushort)) = *(ushort*)lastEntityData;
                    resultOffset = startPos + DiffHeaderSize + _classData.FieldsFlagsSize;
                    
                    for (int i = 0; i < _classData.FieldsCount; i++)
                    {
                        ref var fixedFieldInfo = ref _classData.Fields[i];
                        if (i % 8 == 0)
                        {
                            fields++;
                            *fields = 0;
                        }
                        if (EntityManager.SequenceDiff(_fieldChangeTicks[i], playerTick) > 0)
                        {
                            hasChanges = true;
                            *fields |= (byte)(1 << i%8);
                            Unsafe.CopyBlock(resultData + resultOffset, lastEntityData + lastDataOffset, fixedFieldInfo.Size);
                            resultOffset += fixedFieldInfo.IntSize;
                        }
                        lastDataOffset += fixedFieldInfo.IntSize;
                    }

                    if (!hasChanges)
                        return -1;
                }

                //write totalSize
                int resultSize = resultOffset - result.Length;
                if (resultSize > ushort.MaxValue/2)
                {
                    //request full sync
                    return -1;
                }
                
                *fieldFlagsPtr |= (ushort)(resultSize  << 1);
            }

            result.SetPosition(resultOffset);
            return resultOffset;
        }
    }
}