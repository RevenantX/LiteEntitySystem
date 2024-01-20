namespace LiteEntitySystem.Internal
{
    internal enum DiffResult
    {
        Skip,
        DoneAndDestroy,
        Done
    }

    internal enum SerializerState
    {
        Freed,
        Active,
        Destroyed
    }

    internal struct StateSerializer
    {
        private enum RPCMode
        {
            Normal,
            Sync,
            OwnerChange
        }
        
        public const int HeaderSize = 5;
        private const int TicksToDestroy = 32;
        public const int DiffHeaderSize = 4;
        public const int MaxStateSize = 32767; //half of ushort

        private byte _version;
        private EntityClassData _classData;
        private InternalEntity _entity;
        private byte[] _latestEntityData;
        private ushort[] _fieldChangeTicks;
        private ushort _versionChangedTick;
        private SerializerState _state;
        private ushort _lastWriteTick;
        private RemoteCallPacket _rpcHead;
        private RemoteCallPacket _rpcTail;
        private RemoteCallPacket _syncRpcHead;
        private RemoteCallPacket _syncRpcTail;
        private ushort _ticksOnDestroy;
        private byte _controllerOwner;
        private byte _owner;
        private uint _fullDataSize;
        private int _syncFrame;
        private RPCMode _rpcMode;

        public void AddRpcPacket(RemoteCallPacket rpc)
        {
            //Logger.Log($"AddRpc for tick: {rpc.Header.Tick}, St: {_entity.ServerManager.Tick}, Id: {rpc.Header.Id}");
            
            switch (_rpcMode)
            {
                case RPCMode.OwnerChange:
                case RPCMode.Normal:
                {
                    if (_rpcHead == null)
                        _rpcHead = rpc;
                    else
                        _rpcTail.Next = rpc;
                    _rpcTail = rpc;
                    if (_rpcMode == RPCMode.OwnerChange)
                        rpc.Flags |= ExecuteFlags.SendToOwner;
                    break;
                }

                case RPCMode.Sync:
                {
                    if (_syncRpcHead == null)
                        _syncRpcHead = rpc;
                    else
                        _syncRpcTail.Next = rpc;
                    _syncRpcTail = rpc;
                    break;
                }
            }
        }

        public byte IncrementVersion(ushort tick)
        {
            _lastWriteTick = (ushort)(tick - 1);
            _versionChangedTick = tick;
            return _version++;
        }

        private void CleanPendingRpcs(ref RemoteCallPacket head, ref RemoteCallPacket tail)
        {
            while (head != null)
            {
                _entity.ServerManager.PoolRpc(head);
                var tempNode = head;
                head = head.Next;
                tempNode.Next = null;
            }
            tail = null;
        }

        public unsafe void Init(ref EntityClassData classData, InternalEntity e)
        {
            if (_state != SerializerState.Freed)
            {
                Logger.LogError($"State serializer isn't freed: {_state}");
            }
            _classData = classData;
            _entity = e;
            _state = SerializerState.Active;
            _syncFrame = -1;
            _owner = 0;
            _controllerOwner = 0;
            _rpcMode = RPCMode.Normal;
            
            //wipe previous rpcs
            CleanPendingRpcs(ref _rpcHead, ref _rpcTail);
            CleanPendingRpcs(ref _syncRpcHead, ref _syncRpcTail);

            _fullDataSize = (uint)(HeaderSize + _classData.FixedFieldsSize);
            Utils.ResizeOrCreate(ref _latestEntityData, (int)_fullDataSize);
            Utils.ResizeOrCreate(ref _fieldChangeTicks, classData.FieldsCount);

            fixed (byte* data = _latestEntityData)
            {
                *(ushort*)data = e.Id;
                data[2] = e.Version;
                *(ushort*)(data + 3) = e.ClassId;
            }
        }

        private unsafe void Write(ushort serverTick, ushort minimalTick)
        {
            //write if there new tick
            if (serverTick == _lastWriteTick) 
                return;

            byte controllerOwner = _entity is ControllerLogic controller
                ? controller.InternalOwnerId
                : EntityManager.ServerPlayerId;
            
            //TODO: correct sync on owner change
            if (_controllerOwner != controllerOwner)
            {
                _controllerOwner = controllerOwner;
                _owner = controllerOwner;
                MakeOnSync(serverTick);
            }
            else if (_entity is EntityLogic entityLogic && _owner != entityLogic.InternalOwnerId.Value)
            {
                _owner = entityLogic.InternalOwnerId;
                MakeOnSync(serverTick);
            }
            
            if (Utils.SequenceDiff(minimalTick, _versionChangedTick) > 0)
                _versionChangedTick = minimalTick;

            _lastWriteTick = serverTick;
            fixed (byte* latestEntityData = _latestEntityData)
            {
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    ref var field = ref _classData.Fields[i];
                    byte* latestDataPtr = latestEntityData + HeaderSize + field.FixedOffset;
                    object obj;
                    int offset;
                    
                    //update only changed fields
                    if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        obj = RefMagic.RefFieldValue<SyncableField>(_entity, field.Offset);
                        offset = field.SyncableSyncVarOffset;
                    }
                    else
                    {
                        obj = _entity;
                        offset = field.Offset;
                    }
                    
                    if (field.TypeProcessor.CompareAndWrite(obj, offset, latestDataPtr))
                        _fieldChangeTicks[i] = serverTick;
                    else if (Utils.SequenceDiff(minimalTick, _fieldChangeTicks[i]) > 0)
                        _fieldChangeTicks[i] = minimalTick;
                }
            }
        }

        public void Destroy(ushort serverTick, ushort minimalTick, bool instantly)
        {
            if (_state != SerializerState.Active)
                return;
            if (instantly || serverTick == _versionChangedTick)
            {
                _state = SerializerState.Freed;
                return;
            }
            Write((ushort)(serverTick+1), minimalTick);
            _state = SerializerState.Destroyed;
            _ticksOnDestroy = serverTick;
        }

        public unsafe int GetMaximumSize(ushort forTick)
        {
            if (_state != SerializerState.Active)
                return 0;
            MakeOnSync(forTick);
            int totalSize = (int)_fullDataSize + sizeof(ushort);
            int rpcHeaderSize = sizeof(RPCHeader);
            var rpcNode = _rpcHead;
            while (rpcNode != null)
            {
                totalSize += rpcNode.TotalSize + rpcHeaderSize;
                rpcNode = rpcNode.Next;
            }
            rpcNode = _syncRpcHead;
            while (rpcNode != null)
            {
                totalSize += rpcNode.TotalSize + rpcHeaderSize;
                rpcNode = rpcNode.Next;
            }
            return totalSize;
        }

        private void MakeOnSync(ushort tick)
        {
            if (tick == _syncFrame)
                return;
            _syncFrame = tick;
            CleanPendingRpcs(ref _syncRpcHead, ref _syncRpcTail);
            _rpcMode = RPCMode.Sync;
            for (int i = 0; i < _classData.SyncableFields.Length; i++)
                RefMagic.RefFieldValue<SyncableField>(_entity, _classData.SyncableFields[i].Offset)
                    .OnSyncRequested();
            _rpcMode = RPCMode.Normal;
        }

        //initial state with compression
        private unsafe void WriteInitialState(bool isOwned, ushort serverTick, byte* resultData, ref int position)
        {
            MakeOnSync(serverTick);
            fixed (byte* lastEntityData = _latestEntityData)
                RefMagic.CopyBlock(resultData + position, lastEntityData, _fullDataSize);
            position += (int)_fullDataSize;
            
            //add RPCs count
            ushort* rpcCount = (ushort*)(resultData + position);
            *rpcCount = 0;
            position += sizeof(ushort);

            //actual write sync RPCs
            var rpcNode = _syncRpcHead;
            while (rpcNode != null)
            {
                if ((isOwned && (rpcNode.Flags & ExecuteFlags.SendToOwner) != 0) ||
                    (!isOwned && (rpcNode.Flags & ExecuteFlags.SendToOther) != 0))
                {
                    //put new
                    *(RPCHeader*)(resultData + position) = rpcNode.Header;
                    position += sizeof(RPCHeader);
                    fixed (byte* rpcData = rpcNode.Data)
                        RefMagic.CopyBlock(resultData + position, rpcData, (uint)rpcNode.TotalSize);
                    position += rpcNode.TotalSize;
                    (*rpcCount)++;
                    //Logger.Log($"[Sever] T: {_entity.ServerManager.Tick}, SendRPC Tick: {rpcNode.Header.Tick}, Id: {rpcNode.Header.Id}, EntityId: {_entity.Id}, TypeSize: {rpcNode.Header.TypeSize}, Count: {rpcNode.Header.Count}");
                }
                rpcNode = rpcNode.Next;
            }
        }

        public unsafe void MakeBaseline(byte playerId, ushort serverTick, ushort minimalTick, byte* resultData, ref int position)
        {
            if (_state != SerializerState.Active)
                return;
            
            Write(serverTick, minimalTick);
            if (_controllerOwner != EntityManager.ServerPlayerId && playerId != _controllerOwner)
                return;
            //don't write total size in full sync and fields
            WriteInitialState(_entity.IsControlledBy(playerId), serverTick, resultData, ref position);
            //Logger.Log($"[SEM] SendBaseline for entity: {_entity.Id}, pos: {position}, posAfterData: {position + _fullDataSize}");
        }

        public unsafe DiffResult MakeDiff(byte playerId, ushort serverTick, ushort minimalTick, ushort playerTick, byte* resultData, ref int position)
        {
            if (_state == SerializerState.Freed)
                return DiffResult.Skip;
            
            if (_state == SerializerState.Destroyed && Utils.SequenceDiff(serverTick, _ticksOnDestroy) >= TicksToDestroy)
            {
                _state = SerializerState.Freed;
                return DiffResult.DoneAndDestroy;
            }
            
            Write(serverTick, minimalTick);
            if (_controllerOwner != EntityManager.ServerPlayerId && playerId != _controllerOwner)
                return DiffResult.Skip;

            //make diff
            int startPos = position;
            bool isOwned = _entity.IsControlledBy(playerId);
            
            //at 0 ushort
            ushort* fieldFlagAndSize = (ushort*)(resultData + startPos);
            position += sizeof(ushort);
            
            if (Utils.SequenceDiff(_versionChangedTick, playerTick) > 0)
            {
                //write full header here (totalSize + eid)
                //also all fields
                *fieldFlagAndSize = 1;
                WriteInitialState(isOwned, serverTick, resultData, ref position);
            } 
            else fixed (byte* lastEntityData = _latestEntityData) //make diff
            {
                byte* entityDataAfterHeader = lastEntityData + HeaderSize;
                // -1 for cycle
                byte* fields = resultData + startPos + DiffHeaderSize - 1;
                //put entity id at 2
                *(ushort*)(resultData + position) = *(ushort*)lastEntityData;
                *fieldFlagAndSize = 0;
                position += sizeof(ushort) + _classData.FieldsFlagsSize;
                int positionBeforeDeltaCompression = position;

                //write fields
                for (int i = 0; i < _classData.FieldsCount; i++)
                {
                    if (i % 8 == 0)
                    {
                        fields++;
                        *fields = 0;
                    }

                    ref var field = ref _classData.Fields[i];
                    if (((field.Flags & SyncFlags.OnlyForOwner) != 0 && !isOwned) || 
                        ((field.Flags & SyncFlags.OnlyForOtherPlayers) != 0 && isOwned))
                    {
                        //Logger.Log($"SkipSync: {field.Name}, isOwned: {isOwned}");
                        continue;
                    }
                    if (Utils.SequenceDiff(_fieldChangeTicks[i], playerTick) <= 0)
                    {
                        //Logger.Log($"SkipOld: {field.Name}");
                        //old data
                        continue;
                    }
                    *fields |= (byte)(1 << i % 8);
                    RefMagic.CopyBlock(resultData + position, entityDataAfterHeader + field.FixedOffset, field.Size);
                    position += field.IntSize;
                    //Logger.Log($"WF {_entity.GetType()} f: {_classData.Fields[i].Name}");
                }

                bool hasChanges = position > positionBeforeDeltaCompression;
                //add RPCs count
                ushort* rpcCount = (ushort*)(resultData + position);
                *rpcCount = 0;
                position += sizeof(ushort);

                //actual write rpcs
                var rpcNode = _rpcHead;
                while (rpcNode != null)
                {
                    bool send = (isOwned && (rpcNode.Flags & ExecuteFlags.SendToOwner) != 0) ||
                                (!isOwned && (rpcNode.Flags & ExecuteFlags.SendToOther) != 0);
                    if (send && Utils.SequenceDiff(playerTick, rpcNode.Header.Tick) < 0)
                    {
                        //put new
                        *(RPCHeader*)(resultData + position) = rpcNode.Header;
                        position += sizeof(RPCHeader);
                        fixed (byte* rpcData = rpcNode.Data)
                            RefMagic.CopyBlock(resultData + position, rpcData, (uint)rpcNode.TotalSize);
                        position += rpcNode.TotalSize;
                        (*rpcCount)++;
                        //Logger.Log($"[Sever] T: {_entity.ServerManager.Tick}, SendRPC Tick: {rpcNode.Header.Tick}, Id: {rpcNode.Header.Id}, EntityId: {_entity.Id}, TypeSize: {rpcNode.Header.TypeSize}, Count: {rpcNode.Header.Count}");
                    }
                    else if (Utils.SequenceDiff(rpcNode.Header.Tick, minimalTick) < 0)
                    {
                        if (rpcNode != _rpcHead)
                        {
                            Logger.LogError("MinimalTickNode isn't first!");
                        }
                        //remove old RPCs (they should be at first place)
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
                
                if (*rpcCount == 0 && !hasChanges)
                {
                    position = startPos;
                    return DiffResult.Skip;
                }
            }

            //write totalSize
            int resultSize = position - startPos;
            //Logger.Log($"rsz: {resultSize} e: {_entity.GetType()} eid: {_entity.Id}");
            if (resultSize > MaxStateSize)
            {
                position = startPos;
                Logger.LogError($"Entity {_entity.Id}, Class: {_entity.ClassId} state size is more than: {MaxStateSize}");
                return DiffResult.Skip;
            }
            *fieldFlagAndSize |= (ushort)(resultSize << 1);
            return DiffResult.Done;
        }
    }
}