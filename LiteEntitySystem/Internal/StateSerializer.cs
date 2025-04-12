using System;

namespace LiteEntitySystem.Internal
{
    internal struct StateSerializer
    {
        public static readonly int HeaderSize = Utils.SizeOfStruct<EntityDataHeader>();
        public const int DiffHeaderSize = 4;
        public const int MaxStateSize = 32767; //half of ushort
        
        private EntityFieldInfo[] _fields;
        private int _fieldsCount;
        private int _fieldsFlagsSize;
        private EntityFlags _flags;
        private InternalEntity _entity;
        private byte[] _latestEntityData;
        private ushort[] _fieldChangeTicks;
        private ushort _versionChangedTick;
        private uint _fullDataSize;
        
        public byte NextVersion;
        public ushort LastChangedTick;

        public void AllocateMemory(ref EntityClassData classData, byte[] ioBuffer)
        {
            if (_entity != null)
            {
                Logger.LogError($"State serializer isn't freed: {_entity}");
                return;
            }
            
            _fields = classData.Fields;
            _fieldsCount = classData.FieldsCount;
            _fieldsFlagsSize = classData.FieldsFlagsSize;
            _fullDataSize = (uint)(HeaderSize + classData.FixedFieldsSize);
            _flags = classData.Flags;
            _latestEntityData = ioBuffer;
            
            if (_fieldChangeTicks == null || _fieldChangeTicks.Length < _fieldsCount)
                _fieldChangeTicks = new ushort[_fieldsCount];
        }

        public unsafe void Init(InternalEntity e, ushort tick)
        {
            _entity = e;
            NextVersion = (byte)(_entity.Version + 1);
            _versionChangedTick = tick;
            LastChangedTick = tick;
            
            fixed (byte* data = _latestEntityData)
                *(EntityDataHeader*)data = _entity.DataHeader;
        }
        
        public unsafe void MarkFieldChanged<T>(ushort fieldId, ushort tick, ref T newValue) where T : unmanaged
        {
            LastChangedTick = tick;
            _fieldChangeTicks[fieldId] = tick;
            fixed (byte* data = &_latestEntityData[HeaderSize + _fields[fieldId].FixedOffset])
                *(T*)data = newValue;
        }

        public int GetMaximumSize() =>
            _entity == null ? 0 : (int)_fullDataSize + sizeof(ushort);

        public void MakeNewRPC() =>
            _entity.ServerManager.AddRemoteCall(
                _entity,
                new ReadOnlySpan<byte>(_latestEntityData, 0, HeaderSize),
                RemoteCallPacket.NewRPCId,
                ExecuteFlags.SendToAll);

        public void MakeConstructedRPC()
        {
            //make on sync
            try
            {
                var syncableFields = _entity.ClassData.SyncableFields;
                for (int i = 0; i < syncableFields.Length; i++)
                    RefMagic.RefFieldValue<SyncableField>(_entity, syncableFields[i].Offset)
                        .OnSyncRequested();
                _entity.OnSyncRequested();
            }
            catch (Exception e)
            {
                Logger.LogError($"Exception in OnSyncRequested: {e}");
            }
            
            //actual on constructed rpc
            _entity.ServerManager.AddRemoteCall(
                _entity,
                new ReadOnlySpan<byte>(_latestEntityData, HeaderSize, (int)(_fullDataSize - HeaderSize)),
                RemoteCallPacket.ConstructRPCId,
                ExecuteFlags.SendToAll);
            
            //Logger.Log($"Added constructed RPC: {_entity}");
        }

        //refresh construct rpc with latest values (old behaviour)
        public unsafe void RefreshConstructedRPC(RemoteCallPacket packet)
        {
            fixed(byte* sourceData = _latestEntityData, rawData = packet.Data)
                RefMagic.CopyBlock(rawData, sourceData + HeaderSize, (uint)(_fullDataSize - HeaderSize));
        }

        public void MakeDestroyedRPC()
        {
            LastChangedTick = _entity.EntityManager.Tick;
            _entity.ServerManager.AddRemoteCall(
                _entity,
                RemoteCallPacket.DestroyRPCId,
                ExecuteFlags.SendToAll);
        }

        public void MakeBaseline(byte playerId)
        {
            //skip inactive and other controlled controllers
            if (_entity == null || _entity.IsDestroyed)
                return;
            bool isOwned = _entity.InternalOwnerId.Value == playerId;
            if (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && !isOwned)
                return;
            //don't write total size in full sync and fields
            MakeNewRPC();
            MakeConstructedRPC();
            //Logger.Log($"[SEM] SendBaseline for entity: {_entity.Id}, pos: {position}, posAfterData: {position + _fullDataSize}");
        }

        public void Free()
        {
            _entity = null;
            _latestEntityData = null;
        }

        public unsafe bool MakeDiff(byte playerId, ushort minimalTick, ushort playerTick, byte* resultData, ref int position, HumanControllerLogic playerController)
        {
            if (_entity == null)
            {
                Logger.LogWarning("MakeDiff on freed?");
                return false;
            }

            if (_entity.IsDestroyed && Utils.SequenceDiff(LastChangedTick, minimalTick) < 0)
            {
                Logger.LogError($"Should be removed before: {_entity}");
                return false;
            }

            //refresh minimal tick to prevent errors on tick wrap-arounds
            if (Utils.SequenceDiff(_versionChangedTick, minimalTick) < 0)
                _versionChangedTick = minimalTick;
            
            //skip sync for non owners
            bool isOwned = _entity.InternalOwnerId.Value == playerId;
            if (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && !isOwned)
                return false;
            
            //skip diff sync if disabled
            if (playerController != null && playerController.IsEntityDiffSyncDisabled(new EntitySharedReference(_entity.Id, _entity.Version)))
                return false;
            
            //make diff
            int startPos = position;
            //at 0 ushort
            ushort* totalSize = (ushort*)(resultData + startPos);
            *totalSize = 0;
            
            position += sizeof(ushort);

            fixed (byte* lastEntityData = _latestEntityData) //make diff
            {
                byte* entityDataAfterHeader = lastEntityData + HeaderSize;
                // -1 for cycle
                byte* fields = resultData + startPos + DiffHeaderSize - 1;
                //put entity id at 2
                *(ushort*)(resultData + position) = *(ushort*)lastEntityData;
                position += sizeof(ushort) + _fieldsFlagsSize;
                int positionBeforeDeltaCompression = position;

                //write fields
                for (int i = 0; i < _fieldsCount; i++)
                {
                    if (i % 8 == 0)
                    {
                        fields++;
                        *fields = 0;
                    }
                    
                    //skip very old and increase tick to wrap
                    if (Utils.SequenceDiff(_fieldChangeTicks[i], minimalTick) < 0)
                    {
                        _fieldChangeTicks[i] = minimalTick;
                        continue;
                    }
                    
                    //not actual
                    if (Utils.SequenceDiff(_fieldChangeTicks[i], playerTick) <= 0)
                    {
                        //Logger.Log($"SkipOld: {field.Name}");
                        //old data
                        continue;
                    }

                    ref var field = ref _fields[i];
                    if (((field.Flags & SyncFlags.OnlyForOwner) != 0 && !isOwned) || 
                        ((field.Flags & SyncFlags.OnlyForOtherPlayers) != 0 && isOwned))
                    {
                        //Logger.Log($"SkipSync: {field.Name}, isOwned: {isOwned}");
                        continue;
                    }
                    
                    *fields |= (byte)(1 << i % 8);
                    RefMagic.CopyBlock(resultData + position, entityDataAfterHeader + field.FixedOffset, field.Size);
                    position += field.IntSize;
                    //Logger.Log($"WF {_entity.GetType()} f: {_classData.Fields[i].Name}");
                }

                if (position <= positionBeforeDeltaCompression)
                {
                    position = startPos;
                    return false;
                }
            }

            //write totalSize
            int resultSize = position - startPos;
            //Logger.Log($"rsz: {resultSize} e: {_entity.GetType()} eid: {_entity.Id}");
            if (resultSize > MaxStateSize)
            {
                position = startPos;
                Logger.LogError($"Entity {_entity.Id}, Class: {_entity.ClassId} state size is more than: {MaxStateSize}");
                return false;
            }
            *totalSize = (ushort)resultSize;
            return true;
        }
    }
}