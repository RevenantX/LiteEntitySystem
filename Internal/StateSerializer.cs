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
        private const int HeaderSize = 5;
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
        private int _filledHistory;
        private bool _lagCompensationEnabled;
        private byte _controllerOwner;
        private byte _maxHistory; //should be power of two
        private uint _fullDataSize;
        
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

        public unsafe void Init(ref EntityClassData classData, InternalEntity e)
        {
            _classData = classData;
            _entity = e;
            _state = SerializerState.Active;
            _filledHistory = 0;
            _maxHistory = (byte)e.ServerManager.MaxHistorySize;

            _fullDataSize = (uint)(HeaderSize + _classData.FixedFieldsSize);
            Utils.ResizeOrCreate(ref _latestEntityData, (int)_fullDataSize);
            Utils.ResizeOrCreate(ref _fieldChangeTicks, classData.FieldsCount);
            
            byte* entityPointer = Utils.GetPtr(ref _entity);
            for (int i = 0; i < _classData.SyncableFields.Length; i++)
            {
                ref var syncable = ref Unsafe.AsRef<SyncableField>(entityPointer + _classData.SyncableFields[i].Offset);
                syncable.EntityManager = e.ServerManager;
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

            _history ??= new byte[_maxHistory][];
            if(classData.LagCompensatedSize > 0)
            {
                for (int i = 0; i < _maxHistory; i++)
                    Utils.ResizeOrCreate(ref _history[i], classData.LagCompensatedSize);
            } 
        }

        public unsafe void WriteHistory(ushort tick)
        {
            if (_classData.LagCompensatedSize == 0)
                return;
            _filledHistory = Math.Min(_filledHistory + 1, _maxHistory);
            byte* entityPointer = Utils.GetPtr(ref _entity);
            int historyOffset = 0;
            fixed (byte* history = _history[tick % _maxHistory])
            {
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    ref var field = ref _classData.LagCompensatedFields[i];
                    Unsafe.CopyBlock(history + historyOffset, entityPointer + field.Offset, field.Size);
                    historyOffset += field.IntSize;
                }
            }
        }

        private unsafe void Write(ushort serverTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick || _state != SerializerState.Active) 
                return;

            _controllerOwner = _entity is ControllerLogic controller
                ? controller.InternalOwnerId
                : ServerEntityManager.ServerPlayerId;

            _lastWriteTick = serverTick;
            byte* entityPointer = Utils.GetPtr(ref _entity);
            fixed (byte* latestEntityData = _latestEntityData)
            {
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var field = ref _classData.Fields[i];
                    byte* fieldPtr = entityPointer + field.Offset;
                    byte* latestDataPtr = latestEntityData + HeaderSize + field.FixedOffset;

                    //update only changed fields
                    if(field.FieldType == FieldType.Entity)
                    {
                        ushort entityId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? EntityManager.InvalidEntityId;
                        
                        //local
                        if (entityId >= EntityManager.MaxSyncedEntityCount)
                            entityId = EntityManager.InvalidEntityId;

                        ushort *ushortPtr = (ushort*)latestDataPtr;
                        if (*ushortPtr != entityId)
                        {
                            *ushortPtr = entityId;
                            _fieldChangeTicks[i] = serverTick;
                        }
                    }
                    else if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        ref var syncable = ref Unsafe.AsRef<SyncableField>(fieldPtr);
                        byte* syncVarPtr = Utils.GetPtr(ref syncable) + field.SyncableSyncVarOffset;
                        if (Utils.memcmp(latestDataPtr, syncVarPtr, field.PtrSize) != 0)
                        {
                            Unsafe.CopyBlock(latestDataPtr, syncVarPtr, field.Size);
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

        private unsafe void WriteFull(byte* resultData, byte *lastEntityData, ref int position)
        {
            Unsafe.CopyBlock(resultData + position, lastEntityData, _fullDataSize);
            position += (int)_fullDataSize;
            for (int i = 0; i < _classData.SyncableFields.Length; i++)
            {
                Unsafe.AsRef<SyncableField>(Utils.GetPtr(ref _entity) + _classData.SyncableFields[i].Offset).FullSyncWrite(resultData, ref position);
            }
        }

        public unsafe void MakeBaseline(byte playerId, ushort serverTick, byte* resultData, ref int position)
        {
            if (_state != SerializerState.Active)
                return;
            Write(serverTick);
            if (_controllerOwner != ServerEntityManager.ServerPlayerId && playerId != _controllerOwner)
                return;
            
            //make diff
            fixed (byte* lastEntityData = _latestEntityData)
            {
                //initial state with compression
                //don't write total size in full sync and fields
                WriteFull(resultData, lastEntityData, ref position);
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
            if(_controllerOwner != ServerEntityManager.ServerPlayerId && playerId != _controllerOwner)
                return DiffResult.Skip;

            //make diff
            int startPos = position;

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
                    WriteFull(resultData, lastEntityData, ref position);
                }
                else //make diff
                {
                    byte* entityDataAfterHeader = lastEntityData + HeaderSize;
                    bool localControlled = _entity.IsControlledBy(playerId);
                    bool hasChanges = false;
                    // -1 for cycle
                    byte* fields = resultData + startPos + DiffHeaderSize - 1;
                    //put entity id at 2
                    *(ushort*)(resultData + position) = *(ushort*)lastEntityData;
                    *fieldFlagAndSize = 0;
                    position += sizeof(ushort) + _classData.FieldsFlagsSize;
                    
                    for (int i = 0; i < _classData.FieldsCount; i++)
                    {
                        ref var fieldInfo = ref _classData.Fields[i];
                        if (i % 8 == 0)
                        {
                            fields++;
                            *fields = 0;
                        }
                        
                        if((fieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForLocal) && !localControlled) ||
                           (fieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForRemote) && localControlled))
                            continue;
                        
                        if (Utils.SequenceDiff(_fieldChangeTicks[i], playerTick) > 0)
                        {
                            hasChanges = true;
                            *fields |= (byte)(1 << i%8);
                            Unsafe.CopyBlock(resultData + position, entityDataAfterHeader + fieldInfo.FixedOffset, fieldInfo.Size);
                            position += fieldInfo.IntSize;
                        }
                    }

                    var rpcNode = _rpcHead;
                    while (rpcNode != null)
                    {
                        bool send = (rpcNode.Flags.HasFlagFast(ExecuteFlags.SendToOwner) && localControlled) ||
                                     (rpcNode.Flags.HasFlagFast(ExecuteFlags.SendToOther) && !localControlled);

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
                            _entity.ServerManager.PoolRpc(rpcNode);
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

        public unsafe void EnableLagCompensation(NetPlayer player, ushort tick)
        {
            if (_entity == null || _entity.IsControlledBy(player.Id))
                return;
            var entityLogic = _entity as EntityLogic;
            if (entityLogic == null)
                return;

            int diff = Utils.SequenceDiff(tick, player.StateATick);
            if (diff <= 0 || diff >= _filledHistory || _state != SerializerState.Active)
                return;
            byte* entityPtr = Utils.GetPtr(ref _entity);
            fixed (byte* 
                   historyA = _history[player.StateATick % _maxHistory], 
                   historyB = _history[player.StateBTick % _maxHistory],
                   current = _history[tick % _maxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    var field = _classData.LagCompensatedFields[i];
                    Unsafe.CopyBlock(current + historyOffset, entityPtr + field.Offset, field.Size);
                    if (field.Interpolator != null)
                    {
                        field.Interpolator(
                            historyA + historyOffset,
                            historyB + historyOffset,
                            entityPtr + field.Offset,
                            player.LerpTime);
                    }
                    else
                    {
                        Unsafe.CopyBlock(entityPtr + field.Offset, historyA + historyOffset, field.Size);
                    }
                    historyOffset += field.IntSize;
                }
            }

            entityLogic.OnLagCompensationStart();
            _lagCompensationEnabled = true;
        }

        public unsafe void DisableLagCompensation(ushort tick)
        {
            if (!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            
            byte* entityPtr = Utils.GetPtr(ref _entity);
            fixed (byte* history = _history[tick % _maxHistory])
            {
                int historyOffset = 0;
                for (int i = 0; i < _classData.LagCompensatedFields.Length; i++)
                {
                    var field = _classData.LagCompensatedFields[i];
                    Unsafe.CopyBlock(entityPtr + field.Offset, history + historyOffset, field.Size);
                    historyOffset += field.IntSize;
                }
            }
            ((EntityLogic)_entity).OnLagCompensationEnd();
        }
    }
}