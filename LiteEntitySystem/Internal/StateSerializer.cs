using System.Runtime.CompilerServices;

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
        private bool _lagCompensationEnabled;
        private byte _controllerOwner;
        private uint _fullDataSize;
        private int _syncFrame;

        public void AddRpcPacket(RemoteCallPacket rpc)
        {
            //Logger.Log($"AddRpc for tick: {rpc.Header.Tick}, St: {_entity.ServerManager.Tick}, Id: {rpc.Header.Id}");
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
            _syncFrame = -1;

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
            if (serverTick == _lastWriteTick || _state != SerializerState.Active) 
                return;

            byte currentOwner = _entity is ControllerLogic controller
                ? controller.InternalOwnerId
                : EntityManager.ServerPlayerId;
            if (_controllerOwner != currentOwner)
            {
                _controllerOwner = currentOwner;
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
                        obj = Utils.RefFieldValue<SyncableField>(_entity, field.Offset);
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
            if (instantly)
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
            var rpcNode = _rpcHead;
            int rpcHeaderSize = sizeof(RPCHeader);
            while (rpcNode != null)
            {
                totalSize += rpcNode.TotalSize + rpcHeaderSize;
                rpcNode = rpcNode.Next;
            }
            return totalSize;
        }

        private void MakeOnSync(ushort tick)
        {
            if (_state != SerializerState.Active || tick == _syncFrame)
                return;
            _syncFrame = tick;
            for (int i = 0; i < _classData.SyncableFields.Length; i++)
            {
                var syncableField = _classData.SyncableFields[i];
                var obj = Utils.RefFieldValue<SyncableField>(_entity, syncableField.Offset);
                obj.InternalOnSyncRequested();
            }
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

            //actual write rpcs
            var rpcNode = _rpcHead;
            while (rpcNode != null)
            {
                if ((isOwned && (rpcNode.Flags & ExecuteFlags.SendToOwner) != 0) ||
                    (!isOwned && (rpcNode.Flags & ExecuteFlags.SendToOther) != 0))
                {
                    //put new
                    var header = rpcNode.Header;
                    //refresh tick
                    header.Tick = serverTick;
                    *(RPCHeader*)(resultData + position) = header;
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
            {
                return DiffResult.Skip;
            }

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
                        var header = rpcNode.Header;
                        *(RPCHeader*)(resultData + position) = header;
                        position += sizeof(RPCHeader);
                        fixed (byte* rpcData = rpcNode.Data)
                            RefMagic.CopyBlock(resultData + position, rpcData, (uint)rpcNode.TotalSize);
                        position += rpcNode.TotalSize;
                        (*rpcCount)++;
                        //Logger.Log($"[Sever] T: {_entity.ServerManager.Tick}, SendRPC Tick: {rpcNode.Header.Tick}, Id: {rpcNode.Header.Id}, EntityId: {_entity.Id}, TypeSize: {rpcNode.Header.TypeSize}, Count: {rpcNode.Header.Count}");
                    }
                    else if (Utils.SequenceDiff(rpcNode.Header.Tick, minimalTick) < 0)
                    {
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
            *fieldFlagAndSize |= (ushort)(resultSize << 1);
            return DiffResult.Done;
        }
    }
}