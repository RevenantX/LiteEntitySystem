using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    internal enum DiffResult
    {
        Skip,
        DoneAndDestroy,
        RequestBaselineSync,
        Done
    }

    internal enum SerializerState
    {
        Active,
        Destroyed,
        Freed
    }

    internal struct StateSerializer
    {
        private const int MaxHistory = 32; //should be power of two
        private const int HeaderSize = 5;
        private const int TicksToDestroy = 32;
        
        public const int DiffHeaderSize = 4;
        public const int HeaderWithTotalSize = 7;
        
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
        private int _filledHistory;
        private bool _lagCompensationEnabled;

        public byte ControllerOwner;
        
        private ServerEntityManager Manager => (ServerEntityManager)_entity.EntityManager;

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

        public void Init(ref EntityClassData classData, InternalEntity e)
        {
            _classData = classData;
            _entity = e;
            _state = SerializerState.Active;
            _filledHistory = 0;

            int minimalDataSize = HeaderSize + _classData.FieldsFlagsSize + _classData.FixedFieldsSize;
            Utils.ResizeOrCreate(ref _latestEntityData, minimalDataSize);
            Utils.ResizeOrCreate(ref _fieldChangeTicks, classData.FieldsCount);

            unsafe
            {
                byte* entityPointer = InternalEntity.GetPtr(ref _entity);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    ref var syncable = ref Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].FieldOffset);
                    syncable.EntityManager = Manager;
                    syncable.FieldId = (byte)i;
                    syncable.EntityId = e.Id;
                    syncable.OnServerInitialized();
                }
                
                fixed (byte* data = _latestEntityData)
                {
                    Unsafe.Write(data, e.Id);
                    data[2] = e.Version;
                    Unsafe.Write(data + 3, e.ClassId);
                }
            }

            _history ??= new byte[MaxHistory][];
            if(classData.LagCompensatedSize > 0)
            {
                for (int i = 0; i < MaxHistory; i++)
                    Utils.ResizeOrCreate(ref _history[i], classData.LagCompensatedSize);
            } 
        }

        public unsafe void WriteHistory(ushort tick)
        {
            if (_classData.LagCompensatedSize == 0)
                return;
            _filledHistory = Math.Min(_filledHistory + 1, MaxHistory);
            byte* entityPointer = InternalEntity.GetPtr(ref _entity);
            int historyOffset = 0;
            fixed (byte* history = _history[tick % MaxHistory])
            {
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    ref var field = ref _classData.LagCompensatedFields[i];
                    field.Get(entityPointer, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }

        private unsafe void Write(ushort serverTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick || _state != SerializerState.Active) 
                return;

            ControllerOwner = _entity is ControllerLogic controller
                ? controller.InternalOwnerId
                : ServerEntityManager.ServerPlayerId;

            _lastWriteTick = serverTick;
            byte* entityPointer = InternalEntity.GetPtr(ref _entity);
            fixed (byte* latestEntityData = _latestEntityData)
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
                        
                        //local
                        if (entityId >= EntityManager.MaxEntityCount)
                            entityId = EntityManager.InvalidEntityId;

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

        public unsafe void MakeBaseline(byte playerId, ushort serverTick, byte* resultData, ref int position)
        {
            if (_state != SerializerState.Active)
                return;
            Write(serverTick);
            if (ControllerOwner != ServerEntityManager.ServerPlayerId && playerId != ControllerOwner)
                return;
            
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

                byte* entityPointer = InternalEntity.GetPtr(ref _entity);
                for (int i = 0; i < _classData.SyncableFields.Length; i++)
                {
                    Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].FieldOffset).FullSyncWrite(resultData, ref position);
                }
            }
        }

        public unsafe DiffResult MakeDiff(byte playerId, ushort minimalTick, ushort serverTick, ushort playerTick, byte* resultData, ref int position)
        {
            bool canReuse = false;
            if (_state == SerializerState.Destroyed && Utils.SequenceDiff(serverTick, _ticksOnDestroy) >= TicksToDestroy)
            {
                _state = SerializerState.Freed;
                canReuse = true;
            }
            Write(serverTick);
            if(ControllerOwner != ServerEntityManager.ServerPlayerId && playerId != ControllerOwner)
                return DiffResult.Skip;

            //make diff
            int startPos = position;
            byte* entityPointer = InternalEntity.GetPtr(ref _entity);

            fixed (byte* lastEntityData = _latestEntityData)
            {
                //at 0 ushort
                ushort* fieldFlagAndSize = (ushort*) (resultData + startPos);
                position += 2;
                
                //initial state with compression
                if (Utils.SequenceDiff(_versionChangedTick, playerTick) > 0)
                {
                    //write full header here (totalSize + eid)
                    //also all fields
                    *fieldFlagAndSize = 1;
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
                    //put entity id at 2
                    *(ushort*)(resultData + position) = *(ushort*)lastEntityData;
                    *fieldFlagAndSize = 0;
                    position += sizeof(ushort) + _classData.FieldsFlagsSize;
                    
                    for (int i = 0; i < _classData.FieldsCount; i++)
                    {
                        ref var fixedFieldInfo = ref _classData.Fields[i];
                        if (i % 8 == 0)
                        {
                            fields++;
                            *fields = 0;
                        }
                        if (Utils.SequenceDiff(_fieldChangeTicks[i], playerTick) > 0)
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

                        if (send && Utils.SequenceDiff(playerTick, rpcNode.Tick) < 0)
                        {
                            hasChanges = true;
                            //put new
                            resultData[position] = rpcNode.Id;
                            resultData[position + 1] = rpcNode.FieldId;
                            Unsafe.Write(resultData + position + 2, rpcNode.Tick);
                            Unsafe.Write(resultData + position + 4, rpcNode.Size);
                            fixed (byte* rpcData = rpcNode.Data)
                                Unsafe.CopyBlock(resultData + position + 6, rpcData, rpcNode.Size);
                            position += 6 + rpcNode.Size;
                        }
                        else if (Utils.SequenceDiff(rpcNode.Tick, minimalTick) < 0)
                        {
                            //remove old RPCs
                            Manager.PoolRpc(rpcNode);
                            if (_rpcTail == _rpcHead)
                                _rpcTail = null;
                            _rpcHead = rpcNode.Next;
                            rpcNode.Next = null;
                            rpcNode = _rpcHead;
                            continue;
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
                
                *fieldFlagAndSize |= (ushort)(resultSize << 1);
            }
            return canReuse ? DiffResult.DoneAndDestroy : DiffResult.Done;
        }

        public unsafe void EnableLagCompensation(NetPlayer player)
        {
            if (_entity.IsControlledBy(player.Id))
                return;
            int diff = Utils.SequenceDiff(Manager.Tick, player.StateATick);
            if (diff <= 0 || diff >= _filledHistory || _state != SerializerState.Active)
                return;
            byte* entityPtr = InternalEntity.GetPtr(ref _entity);
            fixed (byte* 
                   historyA = _history[player.StateATick % MaxHistory], 
                   historyB = _history[player.StateBTick % MaxHistory],
                   current = _history[Manager.Tick % MaxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    var field = _classData.LagCompensatedFields[i];
                    field.Get(entityPtr, current + historyOffset);
                    if (field.Interpolator != null)
                    {
                        field.Interpolator(
                            historyA + historyOffset,
                            historyB + historyOffset,
                            entityPtr + field.FieldOffset,
                            player.LerpTime);
                    }
                    else
                    {
                        field.Set(entityPtr, historyA + historyOffset);
                    }
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
            
            byte* entityPtr = InternalEntity.GetPtr(ref _entity);
            fixed (byte* history = _history[Manager.Tick % MaxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    var field = _classData.LagCompensatedFields[i];
                    field.Set(entityPtr, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
            
            if (_entity is EntityLogic entityLogic)
                entityLogic.OnLagCompensation(false);
        }
    }
}