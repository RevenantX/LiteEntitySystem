using System;

namespace LiteEntitySystem.Internal
{
    struct FieldChangedData
    {
        public ushort Tick;
        public EntityState State;

        public FieldChangedData(ushort tick, EntityState state)
        {
            Tick = tick;
            State = state;
        }
    }

    internal struct StateSerializer
    {
        public static readonly ushort HeaderSize = (ushort)Utils.SizeOfStruct<EntityDataHeader>();

        private static byte[] ZeroArray = new byte[1024];

        public const int DiffHeaderSize = 4;
        public const int MaxStateSize = 32767; //half of ushort
        
        private const int TickBetweenFullRefresh = ushort.MaxValue/5;
        
        private EntityFieldInfo[] _fields;
        private int _fieldsCount;
        private int _fieldsFlagsSize;
        private EntityFlags _flags;
        private InternalEntity _entity;
        private byte[] _latestEntityData;
        private FieldChangedData[] _fieldChangeTicks;
        private ushort _versionChangedTick;
        private uint _fullDataSize;
        private DateTime _lastRefreshedTime;
        private int _secondsBetweenRefresh;
        
        public byte NextVersion;
        public ushort LastChangedTick;
        
        /// <summary>
        /// Get maximum delta size
        /// </summary>
        /// <returns>FullDataSize + ushort(size of data) + ushort(entityId) + _fieldFlagsSize</returns>
        public int MaximumSize =>
            _entity == null ? 0 : (int)_fullDataSize + sizeof(ushort) + sizeof(ushort) + _fieldsFlagsSize;
        
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
                _fieldChangeTicks = new FieldChangedData[_fieldsCount];
        }

        public unsafe void Init(InternalEntity e, ushort tick)
        {
            _entity = e;
            NextVersion = (byte)(_entity.Version + 1);
            _versionChangedTick = tick;
            LastChangedTick = tick;
            
            fixed (byte* data = _latestEntityData)
                *(EntityDataHeader*)data = _entity.DataHeader;

            _lastRefreshedTime = DateTime.UtcNow;
            _secondsBetweenRefresh = TickBetweenFullRefresh / e.ServerManager.Tickrate;
        }
        
        public unsafe void UpdateFieldValue<T>(ushort fieldId, ushort minimalTick, ushort tick, ref T newValue) where T : unmanaged
        {
            _fieldChangeTicks[fieldId] = new FieldChangedData(tick, _entity?.CreationState ?? EntityState.New);
            MarkChanged(minimalTick, tick);
            fixed (byte* data = &_latestEntityData[HeaderSize + _fields[fieldId].FixedOffset])
                *(T*)data = newValue;
        }
        
        public void MarkFieldsChanged(ushort minimalTick, ushort tick, SyncFlags onlyWithFlags)
        {
            for (int i = 0; i < _fieldsCount; i++)
                if ((_fields[i].Flags & onlyWithFlags) != 0)
                    _fieldChangeTicks[i] = new FieldChangedData(tick, _entity.CreationState);
            MarkChanged(minimalTick, tick);
        }

        public void MarkChanged(ushort minimalTick, ushort tick)
        {
            LastChangedTick = tick;
            //refresh every X seconds to prevent wrap-around bugs
            DateTime currentTime = DateTime.UtcNow;
            if ((currentTime - _lastRefreshedTime).TotalSeconds > _secondsBetweenRefresh)
            {
                _versionChangedTick = minimalTick;

                var creationState = _entity?.CreationState ?? EntityState.New;
                for (int i = 0; i < _fieldsCount; i++)
                    if(_fieldChangeTicks[i].Tick != tick || _fieldChangeTicks[i].State < creationState) //change only not refreshed at current tick
                        _fieldChangeTicks[i] = new FieldChangedData(minimalTick, creationState);
                _lastRefreshedTime = currentTime;
            }
        }

        public RemoteCallPacket MakeNewRPC(NetPlayer forPlayer) =>
            _entity.ServerManager.AddRemoteCall(
                forPlayer,
                _entity,
                (ushort)InternalRPCType.New,
                ExecuteFlags.SendToAll);

        public RemoteCallPacket MakeConstructedRPC(NetPlayer forPlayer) =>
            _entity.ServerManager.AddRemoteCall(
                forPlayer,
                _entity,
                (ushort)InternalRPCType.Construct,
                ExecuteFlags.SendToAll);

        public unsafe void RefreshNewRPC(NetPlayer forPlayer, RemoteCallPacket packet)
        {
            Utils.ResizeOrCreate(ref packet.Data, MaximumSize);

            RefreshSyncGroupsVariable(forPlayer);
            
            fixed (byte* lastEntityData = _latestEntityData, resultData = packet.Data)
            {
                //copy header
                RefMagic.CopyBlock(resultData, lastEntityData, HeaderSize);
                //make diff between default data
                byte* entityDataAfterHeader = lastEntityData + HeaderSize;
                
                // -1 for cycle
                int writePosition = HeaderSize + _fieldsFlagsSize;
                byte* fields = resultData + HeaderSize - 1;

                //write fields
                for (int i = 0; i < _fieldsCount; i++)
                {
                    if (i % 8 == 0) //reset next byte each 8 bits
                        *++fields = 0;

                    ref var field = ref _fields[i];
                    //compare with 0
                    if(field.IntSize > ZeroArray.Length)
                        ZeroArray = new byte[field.IntSize];

                    var zeroArraySpan = new ReadOnlySpan<byte>(ZeroArray, 0, field.IntSize);
                    if (new ReadOnlySpan<byte>(entityDataAfterHeader + field.FixedOffset, field.IntSize).SequenceEqual(zeroArraySpan))
                        continue;
                    
                    *fields |= (byte)(1 << i % 8);        
                    RefMagic.CopyBlock(resultData + writePosition, entityDataAfterHeader + field.FixedOffset, field.Size);
                    writePosition += field.IntSize;
                }

                if (writePosition > MaxStateSize)
                {
                    Logger.LogError($"Entity {_entity.Id}, Class: {_entity.ClassId} state size is more than: {MaxStateSize}");
                    writePosition = HeaderSize;
                }
                
                //update info after resize for correct buffer allocation
                int prevTotalSize = packet.TotalSize;
                packet.Header.ByteCount = (ushort)writePosition;
                forPlayer.NotifyRPCResized(prevTotalSize, packet.TotalSize);

                //if (writePosition > HeaderSize + _fieldsFlagsSize)
                //Logger.Log($"NewRPC bytes (server) eid:{_entity.Id} cls:{_entity.ClassId} len:{writePosition} data:{Utils.BytesToHexString(new ReadOnlySpan<byte>(resultData, writePosition))}");
            }
        }
        
        //refresh construct rpc with latest values (old behaviour)
        public unsafe void RefreshConstructedRPC(NetPlayer forPlayer, RemoteCallPacket packet, bool skipDelta)
        {
            //make on sync
            try
            {
                var syncableFields = _entity.ClassData.SyncableFields;
                for (int i = 0; i < syncableFields.Length; i++)
                    RefMagic.GetFieldValue<SyncableField>(_entity, syncableFields[i].Offset).OnSyncRequested();
                _entity.OnSyncRequested();
            }
            catch (Exception e)
            {
                Logger.LogError($"Exception in OnSyncRequested: {e}");
            }

            if(skipDelta)
                return;

            bool isOwned = _entity.InternalOwnerId.Value == forPlayer.Id;
            Utils.ResizeOrCreate(ref packet.Data, MaximumSize);
            var enabledSyncGroups = RefreshSyncGroupsVariable(forPlayer);
            
            fixed (byte* lastEntityData = _latestEntityData, resultData = packet.Data)
            {
                byte* entityDataAfterHeader = lastEntityData + HeaderSize;     
                // -1 for cycle
                int writePosition = _fieldsFlagsSize;
                byte* fields = resultData - 1;

                //write fields
                for (int i = 0; i < _fieldsCount; i++)
                {
                    if (i % 8 == 0) //reset next byte each 8 bits
                        *++fields = 0;

                    //not actual
                    if (_fieldChangeTicks[i].State == EntityState.New)
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
                    
                    if(!isOwned && SyncGroupUtils.IsSyncVarDisabled(enabledSyncGroups, field.Flags))
                    {
                        //IgnoreDiffSyncSettings
                        continue;
                    }
                    
                    *fields |= (byte)(1 << i % 8);
                    RefMagic.CopyBlock(resultData + writePosition, entityDataAfterHeader + field.FixedOffset, field.Size);
                    writePosition += field.IntSize;
                }

                if (writePosition > MaxStateSize)
                {
                    Logger.LogError($"Entity {_entity.Id}, Class: {_entity.ClassId} state size is more than: {MaxStateSize}");
                    writePosition = HeaderSize;
                }
                
                //update info after resize for correct buffer allocation
                int prevTotalSize = packet.TotalSize;
                packet.Header.ByteCount = (ushort)writePosition;
                forPlayer.NotifyRPCResized(prevTotalSize, packet.TotalSize);
            }
        }
        
        public SyncGroup RefreshSyncGroupsVariable(NetPlayer player)
        {
            SyncGroup enabledGroups = SyncGroup.All;
            if (_entity is EntityLogic el)
            {
                if (player.EntitySyncInfo.TryGetValue(el, out var syncGroupData))
                {
                    enabledGroups = syncGroupData.EnabledGroups;
                    _fieldChangeTicks[el.IsSyncEnabledFieldId] = new FieldChangedData(syncGroupData.LastChangedTick, _entity.CreationState);
                }
                else
                {
                    //if no data it "never" changed
                    _fieldChangeTicks[el.IsSyncEnabledFieldId] = new FieldChangedData(_versionChangedTick, _entity.CreationState);
                }
                _latestEntityData[HeaderSize + _fields[el.IsSyncEnabledFieldId].FixedOffset] = (byte)enabledGroups;
            }

            return enabledGroups;
        }

        public void MakeDestroyedRPC(ushort tick)
        {
            //Logger.Log($"DestroyEntity: {_entity.Id} {_entity.Version}, ClassId: {_entity.ClassId}");
            LastChangedTick = tick;
            _entity.ServerManager.AddRemoteCall(
                null,
                _entity,
                (ushort)InternalRPCType.Destroy,
                ExecuteFlags.SendToAll);
        }

        public bool ShouldSync(byte playerId, bool includeDestroyed)
        {
            if (_entity == null || (!includeDestroyed && _entity.IsDestroyed))
                return false;
            if (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && _entity.InternalOwnerId.Value != playerId)
                return false;
            return true;
        }

        public void Free()
        {
            _entity = null;
            _latestEntityData = null;
        }

        public unsafe bool MakeDiff(NetPlayer player, ushort minimalTick, byte* resultData, ref int position)
        {
            if (_entity == null)
            {
                Logger.LogWarning("MakeDiff on freed?");
                return false;
            }
            
            //skip known
            if (Utils.SequenceDiff(LastChangedTick, player.LatestServerTick) <= 0)
                return false;
            
            if (_entity.IsDestroyed && Utils.SequenceDiff(LastChangedTick, minimalTick) < 0)
            {
                Logger.LogError($"Should be removed before: {_entity}");
                return false;
            }
            
            //skip sync for non owners
            bool isOwned = _entity.InternalOwnerId.Value == player.Id;
            if (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && !isOwned)
                return false;
            
            //make diff
            int startPos = position;
            //at 0 ushort
            ushort* totalSize = (ushort*)(resultData + startPos);
            *totalSize = 0;
            
            position += sizeof(ushort);

            //if constructed not received send difference from constructed. Else from last state
            ushort compareToTick = Utils.SequenceDiff(_versionChangedTick, player.LatestServerTick) > 0
                ? _versionChangedTick
                : player.LatestServerTick;
            
            //overwrite IsSyncEnabled for each player
            SyncGroup enabledSyncGroups = RefreshSyncGroupsVariable(player);

            fixed (byte* lastEntityData = _latestEntityData) //make diff
            {
                byte* entityDataAfterHeader = lastEntityData + HeaderSize;
                
                // -1 for cycle
                byte* fields = resultData + startPos + DiffHeaderSize - 1;
                //put entity id at 2
                *(ushort*)(resultData + position) = _entity.Id;
                position += sizeof(ushort) + _fieldsFlagsSize;
                int positionBeforeDeltaCompression = position;

                //write fields
                for (int i = 0; i < _fieldsCount; i++)
                {
                    if (i % 8 == 0) //reset next byte each 8 bits
                        *++fields = 0;
                    
                    //not actual
                    if (Utils.SequenceDiff(_fieldChangeTicks[i].Tick, compareToTick) <= 0)
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
                    
                    if(!isOwned && SyncGroupUtils.IsSyncVarDisabled(enabledSyncGroups, field.Flags))
                    {
                        //IgnoreDiffSyncSettings
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