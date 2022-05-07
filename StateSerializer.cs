using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    using InternalEntity = EntityManager.InternalEntity;
    
    internal enum DiffResult
    {
        Skip,
        DoneAndDestroy,
        RequestBaselineSync,
        Done
    }

    internal enum SerializerState
    {
        Ignore,
        Active,
        Destroyed,
        Freed
    }
    
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
        private SerializerState _state;

        private class RemoteCallPacket
        {
            public byte Id = byte.MaxValue;
            public byte FieldId = byte.MaxValue;
            public ushort Tick;
            public byte[] Data;
            public ushort Size;
            public ExecuteFlags Flags;
            public RemoteCallPacket Next;

            public void Setup(byte id, byte fieldId, ExecuteFlags flags, ushort tick, int size)
            {
                Id = id;
                FieldId = fieldId;
                Tick = tick;
                Size = (ushort)size;
                Utils.ResizeOrCreate(ref Data, size);
                Next = null;
                Flags = flags;
            }
        }
        
        private byte[] _packets;
        private int _packetsCount;
        private ushort _lastWriteTick;

        private RemoteCallPacket _rpcHead;
        private RemoteCallPacket _rpcTail;
        private readonly Queue<RemoteCallPacket> _rpcPool = new Queue<RemoteCallPacket>();
        private const int TicksToDestroy = 32;
        private ushort _ticksOnDestroy;

        private void AddRpcPacket(RemoteCallPacket rpc)
        {
            if (_rpcHead == null)
                _rpcHead = rpc;
            else
                _rpcTail.Next = rpc;
            _rpcTail = rpc;
        }
        
        public unsafe void AddRemoteCall<T>(T value, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Setup(remoteCallInfo.Id, byte.MaxValue, remoteCallInfo.Flags, _entityLogic.EntityManager.Tick, remoteCallInfo.DataSize);
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            AddRpcPacket(rpc);
        }
        
        public unsafe void AddRemoteCall<T>(T[] value, int count, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Setup(remoteCallInfo.Id, byte.MaxValue, remoteCallInfo.Flags, _entityLogic.EntityManager.Tick, remoteCallInfo.DataSize * count);
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            AddRpcPacket(rpc);
        }

        public unsafe void AddSyncableCall<T>(SyncableField field, T value, MethodInfo method) where T : struct
        {
            var remoteCallInfo = _classData.SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Setup(
                remoteCallInfo.Id, 
                field.FieldId, 
                ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner, 
                _entityLogic.EntityManager.Tick, 
                remoteCallInfo.DataSize);
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            AddRpcPacket(rpc);
        }
        
        public unsafe void AddSyncableCall<T>(SyncableField field, T[] value, int count, MethodInfo method) where T : struct
        {
            var remoteCallInfo = _classData.SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Setup(
                remoteCallInfo.Id, 
                field.FieldId, 
                ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner, 
                _entityLogic.EntityManager.Tick,
                remoteCallInfo.DataSize * count);
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            AddRpcPacket(rpc);
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
            {
                _state = SerializerState.Ignore;
                return;
            }
            _state = SerializerState.Active;

            int minimalDataSize = HeaderSize + _classData.FieldsFlagsSize + _classData.FixedFieldsSize;
            Utils.ResizeOrCreate(ref _latestEntityData, minimalDataSize);
            Utils.ResizeOrCreate(ref _fieldChangeTicks, classData.FieldsCount);

            unsafe
            {
                byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    ref var syncable = ref Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset);
                    syncable.Serializer = this;
                    syncable.FieldId = (byte)i;
                }
                
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

        public unsafe void Write(ushort serverTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick || _state != SerializerState.Active) 
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
                    if(entityFieldInfo.IsEntity)
                    {
                        ushort entityId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? EntityManager.InvalidEntityId;
                        ushort *ushortPtr = (ushort*)latestDataPtr;
                        if (*ushortPtr != entityId)
                        {
                            *ushortPtr = entityId;
                            _fieldChangeTicks[i] = serverTick;
                        }
                    }
                    else
                    {
                        if (Utils.memcmp(latestDataPtr, fieldPtr, entityFieldInfo.PtrSize) != 0)
                        {
                            Unsafe.CopyBlock(latestDataPtr, fieldPtr, entityFieldInfo.Size);
                            _fieldChangeTicks[i] = serverTick;
                        }
                    }
                    offset += entityFieldInfo.IntSize;
                }
            }
        }

        public void Destroy(ushort serverTick)
        {
            Write((ushort)(serverTick+1));
            _state = SerializerState.Destroyed;
            _ticksOnDestroy = serverTick;
        }

        public unsafe void MakeBaseline(ushort serverTick, byte* resultData, ref int position)
        {
            if (_state != SerializerState.Active)
                return;
            Write(serverTick);
            
            //make diff
            int lastDataOffset = HeaderSize;

            fixed (byte* lastEntityData = _latestEntityData)
            {
                //initial state with compression
                //don't write total size in full sync and fields
                //totalSizePos here equal to EID position
                //set fields to sync all
                Unsafe.CopyBlock(resultData + position, lastEntityData, HeaderSize);
                position += HeaderSize;
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var fixedFieldInfo = ref _classData.Fields[i];
                    Unsafe.CopyBlock(
                        resultData + position, 
                        lastEntityData + lastDataOffset,
                        fixedFieldInfo.Size);
                    position += fixedFieldInfo.IntSize;
                    lastDataOffset += fixedFieldInfo.IntSize;
                }

                byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset).FullSyncWrite(resultData, ref position);
                }
            }
        }

        public unsafe DiffResult MakeDiff(byte playerId, ushort minimalTick, ushort serverTick, ushort playerTick, byte* resultData, ref int position)
        {
            if (_state == SerializerState.Ignore)
                return DiffResult.Skip;
            bool canReuse = false;
            if (_state == SerializerState.Destroyed && EntityManager.SequenceDiff(serverTick, _ticksOnDestroy) >= TicksToDestroy)
            {
                _state = SerializerState.Freed;
                canReuse = true;
            }
            Write(serverTick);

            //make diff
            int startPos = position;
            int lastDataOffset = HeaderSize;
            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entityLogic);

            fixed (byte* lastEntityData = _latestEntityData)
            {
                ushort* fieldFlagsPtr = (ushort*) (resultData + startPos);
                //initial state with compression
                if (EntityManager.SequenceDiff(_versionChangedTick, playerTick) > 0)
                {
                    //write full header here (totalSize + eid)
                    //also all fields
                    *fieldFlagsPtr = 1;
                    Unsafe.CopyBlock(resultData + startPos + sizeof(ushort), lastEntityData, HeaderSize);
                    position += HeaderWithTotalSize;
                    for (int i = 0; i < _classData.FieldsCount; i++)
                    {
                        ref var fixedFieldInfo = ref _classData.Fields[i];
                        Unsafe.CopyBlock(
                            resultData + position, 
                            lastEntityData + lastDataOffset,
                            fixedFieldInfo.Size);
                        position += fixedFieldInfo.IntSize;
                        lastDataOffset += fixedFieldInfo.IntSize;
                    }
                    for (int i = 0; i < _classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset).FullSyncWrite(resultData, ref position);
                    }
                }
                else //make diff
                {
                    bool hasChanges = false;
                    // -1 for cycle
                    byte* fields = resultData + startPos + DiffHeaderSize - 1;
                    //put entity id
                    *(ushort*)(resultData + startPos + sizeof(ushort)) = *(ushort*)lastEntityData;
                    *fieldFlagsPtr = 0;
                    position += DiffHeaderSize + _classData.FieldsFlagsSize;
                    
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
                            Unsafe.CopyBlock(resultData + position, lastEntityData + lastDataOffset, fixedFieldInfo.Size);
                            position += fixedFieldInfo.IntSize;
                        }
                        lastDataOffset += fixedFieldInfo.IntSize;
                    }
                    var rpcNode = _rpcHead;
                    while (rpcNode != null)
                    {
                        bool send = ((rpcNode.Flags & ExecuteFlags.SendToOwner) != 0 &&
                                     _entityLogic.IsControlledBy(playerId)) ||
                                     ((rpcNode.Flags & ExecuteFlags.SendToOther) != 0 &&
                                     !_entityLogic.IsControlledBy(playerId));

                        if (send && EntityManager.SequenceDiff(playerTick, rpcNode.Tick) < 0)
                        {
                            hasChanges = true;
                            //put new
                            resultData[position] = rpcNode.Id;
                            resultData[position + 1] = rpcNode.FieldId;
                            *(ushort*)(resultData + position + 2) = rpcNode.Tick;
                            *(ushort*)(resultData + position + 4) = rpcNode.Size;
                            fixed (byte* rpcData = rpcNode.Data)
                            {
                                Unsafe.CopyBlock(resultData + position + 6, rpcData, rpcNode.Size);
                            }
                            position += 6 + rpcNode.Size;
                        }
                        else if (EntityManager.SequenceDiff(rpcNode.Tick, minimalTick) < 0)
                        {
                            //remove old RPCs
                            _rpcPool.Enqueue(rpcNode);
                            if (_rpcTail == _rpcHead)
                                _rpcTail = null;
                            _rpcHead = rpcNode.Next;
                        }
                        rpcNode = rpcNode.Next;
                    }

                    if (!hasChanges)
                    {
                        position = startPos;
                        return DiffResult.Skip;
                    }
                }

                //write totalSize
                int resultSize = position - startPos;
                if (resultSize > ushort.MaxValue/2)
                {
                    //request full sync
                    position = startPos;
                    return DiffResult.RequestBaselineSync;
                }
                
                *fieldFlagsPtr |= (ushort)(resultSize  << 1);
            }
            return canReuse ? DiffResult.DoneAndDestroy : DiffResult.Done;
        }
    }
}