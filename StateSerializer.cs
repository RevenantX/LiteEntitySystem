using System;
using System.Runtime.CompilerServices;

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

    internal struct StateSerializer
    {
        private const int MaxHistory = 64; //should be power of two
        private const int HeaderSize = 5;
        private const int HeaderWithTotalSize = 7;
        private const int TicksToDestroy = 32;
        public const int DiffHeaderSize = 4;
        
        private byte _version;
        private EntityClassData _classData;
        private InternalEntity _entity;
        private byte[][] _history;
        private byte[] _latestEntityData;
        private ushort[] _fieldChangeTicks;
        private ushort _versionChangedTick;
        private SerializerState _state;
        private byte[] _packets;
        private int _packetsCount;
        private ushort _lastWriteTick;
        private RemoteCallPacket _rpcHead;
        private RemoteCallPacket _rpcTail;
        private ushort _ticksOnDestroy;
        private ServerEntityManager _entityManager => (ServerEntityManager)_entity.EntityManager;

        public void AddRpcPacket(RemoteCallPacket rpc)
        {
            if (_rpcHead == null)
                _rpcHead = rpc;
            else
                _rpcTail.Next = rpc;
            _rpcTail = rpc;
        }

        public byte IncrementVersion(ushort tick)
        {
            _lastWriteTick = (ushort)(tick - 1);
            _versionChangedTick = tick;
            return _version++;
        }

        public void Init(EntityClassData classData, InternalEntity e)
        {
            _classData = classData;
            _entity = e;
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
                byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entity);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    ref var syncable = ref Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].FieldOffset);
                    syncable.EntityManager = _entityManager;
                    syncable.FieldId = (byte)i;
                    syncable.EntityId = e.Id;
                }
                
                fixed (byte* data = _latestEntityData)
                {
                    *(ushort*) (data) = e.Id;
                    data[2] = e.Version;
                    *(ushort*) (data + 3) = e.ClassId;
                }
            }
            
            _history ??= new byte[MaxHistory][];
            if(classData.LagCompensatedSize > 0)
            {
                for (int i = 0; i < MaxHistory; i++)
                    Utils.ResizeOrCreate(ref _history[i], classData.LagCompensatedSize);
            } 
        }

        public unsafe void WriteHistory()
        {
            if (_classData.LagCompensatedSize == 0)
                return;
            
            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entity);
            int historyOffset = 0;
            fixed (byte* history = _history[_entityManager.Tick % MaxHistory])
            {
                for (int i = 0; i < _classData.LagCompensatedIndexes.Length; i++)
                {
                    ref var field = ref _classData.Fields[_classData.LagCompensatedIndexes[i]];
                    Unsafe.CopyBlock(history + historyOffset, entityPointer + field.FieldOffset, field.Size);
                    historyOffset += field.IntSize;
                }
            }
        }

        private unsafe void Write(ushort serverTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick || _state != SerializerState.Active) 
                return;
            _lastWriteTick = serverTick;
            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entity);
            fixed (byte* latestEntityData = _latestEntityData, history = _history[serverTick % MaxHistory])
            {
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var field = ref _classData.Fields[i];
                    byte* fieldPtr = entityPointer + field.FieldOffset;
                    byte* latestDataPtr = latestEntityData + HeaderSize + field.FixedOffset;

                    //update only changed fields
                    if(field.IsEntity)
                    {
                        ushort entityId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? EntityManager.InvalidEntityId;
                        ushort *ushortPtr = (ushort*)latestDataPtr;
                        if (*ushortPtr != entityId)
                        {
                            *ushortPtr = entityId;
                            _fieldChangeTicks[i] = serverTick;
                        }
                    }
                    else if (Utils.memcmp(latestDataPtr, fieldPtr, field.PtrSize) != 0)
                    {
                        Unsafe.CopyBlock(latestDataPtr, fieldPtr, field.Size);
                        _fieldChangeTicks[i] = serverTick;
                    }
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
                    ref var field = ref _classData.Fields[i];
                    Unsafe.CopyBlock(
                        resultData + position, 
                        lastEntityData + HeaderSize + field.FixedOffset,
                        field.Size);
                    position += field.IntSize;
                }

                byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entity);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].FieldOffset).FullSyncWrite(resultData, ref position);
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
            byte* entityPointer = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref _entity);

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
                            lastEntityData + HeaderSize + fixedFieldInfo.FieldOffset,
                            fixedFieldInfo.Size);
                        position += fixedFieldInfo.IntSize;
                    }
                    for (int i = 0; i < _classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].FieldOffset).FullSyncWrite(resultData, ref position);
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
                            Unsafe.CopyBlock(resultData + position, lastEntityData + HeaderSize + fixedFieldInfo.FixedOffset, fixedFieldInfo.Size);
                            position += fixedFieldInfo.IntSize;
                        }
                    }
                    var rpcNode = _rpcHead;
                    while (rpcNode != null)
                    {
                        bool send = ((rpcNode.Flags & ExecuteFlags.SendToOwner) != 0 &&
                                     _entity.IsControlledBy(playerId)) ||
                                     ((rpcNode.Flags & ExecuteFlags.SendToOther) != 0 &&
                                     !_entity.IsControlledBy(playerId));

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
                            _entityManager.PoolRpc(rpcNode);
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

        private bool _lagCompensationEnabled;
        
        public unsafe void EnableLagCompensation(ushort playerServerTick)
        {
            int diff = EntityManager.SequenceDiff(_lastWriteTick, playerServerTick);
            if (diff <= 0 || diff >= MaxHistory)
                return;
            byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref _entity);
            fixed (byte* history = _history[playerServerTick % MaxHistory], current = _history[_entityManager.Tick % MaxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedIndexes.Length; i++)
                {
                    var field = _classData.Fields[_classData.LagCompensatedIndexes[i]];
                    Unsafe.CopyBlock(current + historyOffset, entityPtr + field.FieldOffset, field.Size);
                    Unsafe.CopyBlock(entityPtr + field.FieldOffset, history + historyOffset, field.Size);
                    historyOffset += field.IntSize;
                }
            }

            if (_entity is EntityLogic entityLogic)
                entityLogic.OnLagCompensation(true);
            _lagCompensationEnabled = true;
        }

        public unsafe void DisableLagCompensation()
        {
            if (!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            
            byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref _entity);
            fixed (byte* history = _history[_entityManager.Tick % MaxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedIndexes.Length; i++)
                {
                    var field = _classData.Fields[_classData.LagCompensatedIndexes[i]];
                    Unsafe.CopyBlock(entityPtr + field.FieldOffset, history + historyOffset, field.Size);
                    historyOffset += field.IntSize;
                }
            }
            
            if (_entity is EntityLogic entityLogic)
                entityLogic.OnLagCompensation(false);
        }
    }
}