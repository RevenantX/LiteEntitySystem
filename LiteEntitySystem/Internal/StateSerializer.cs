using System;
using System.Collections.Generic;
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
        
        public static readonly int HeaderSize = Helpers.SizeOfStruct<EntityDataHeader>();
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
        private SerializerState _state;
        private RemoteCallPacket _rpcHead;
        private RemoteCallPacket _rpcTail;
        private RemoteCallPacket _syncRpcHead;
        private RemoteCallPacket _syncRpcTail;
        private uint _fullDataSize;
        private int _syncFrame;
        private RPCMode _rpcMode;
        public byte NextVersion => (byte)(_entity?.Version + 1 ?? 0);

        public SerializerState State => _state;

        public ushort LastChangedTick;

        public void AddRpcPacket(RemoteCallPacket rpc)
        {
            LastChangedTick = _entity.EntityManager.Tick;
            
            //Logger.Log($"AddRpc for tick: {rpc.Header.Tick}, St: {_entity.ServerManager.Tick}, Id: {rpc.Header.Id}");
            switch (_rpcMode)
            {
                case RPCMode.OwnerChange:
                case RPCMode.Normal:
                    if (_rpcHead == null)
                        _rpcHead = rpc;
                    else
                        _rpcTail.Next = rpc;
                    _rpcTail = rpc;
                    if (_rpcMode == RPCMode.OwnerChange)
                        rpc.Flags |= ExecuteFlags.SendToOwner;
                    break;

                case RPCMode.Sync:
                    if (_syncRpcHead == null)
                        _syncRpcHead = rpc;
                    else
                        _syncRpcTail.Next = rpc;
                    _syncRpcTail = rpc;
                    break;
            }
        }

        private void CleanPendingRpcs(ref RemoteCallPacket head, out RemoteCallPacket tail)
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

        public void AllocateMemory(ref EntityClassData classData)
        {
            if (_state != SerializerState.Freed)
                Logger.LogError($"State serializer isn't freed: {_state}");

            
            _fields = classData.Fields;
            _fieldsCount = classData.FieldsCount;
            _fieldsFlagsSize = classData.FieldsFlagsSize;
            _fullDataSize = (uint)(HeaderSize + classData.FixedFieldsSize);
            _flags = classData.Flags;
            
            //resize or clean prev data
            if (_latestEntityData == null || _latestEntityData.Length < _fullDataSize)
                _latestEntityData = new byte[_fullDataSize];
            else
                Array.Clear(_latestEntityData, HeaderSize, classData.FixedFieldsSize);
            
            if (_fieldChangeTicks == null || _fieldChangeTicks.Length < _fieldsCount)
                _fieldChangeTicks = new ushort[_fieldsCount];
        }

        public unsafe void Init(InternalEntity e, ushort tick)
        {
            _entity = e;
            _state = SerializerState.Active;
            _syncFrame = -1;
            _rpcMode = RPCMode.Normal;
            _versionChangedTick = tick;
            LastChangedTick = tick;

            //wipe previous rpcs
            CleanPendingRpcs(ref _rpcHead, out _rpcTail);
            CleanPendingRpcs(ref _syncRpcHead, out _syncRpcTail);
            
            fixed (byte* data = _latestEntityData)
                *(EntityDataHeader*)data = _entity.DataHeader;
        }

        public void MarkOwnerChanged(ushort tick)
        {
            LastChangedTick = tick;
            MakeOnSync(tick);
            for (int i = 0; i < _fieldsCount; i++)
                _fieldChangeTicks[i] = tick;
        }
        
        public unsafe void MarkFieldChanged<T>(ushort fieldId, ushort tick, ref T newValue) where T : unmanaged
        {
            LastChangedTick = tick;
            _fieldChangeTicks[fieldId] = tick;
            fixed (byte* data = &_latestEntityData[HeaderSize + _fields[fieldId].FixedOffset])
            {
                if (typeof(T) == typeof(EntitySharedReference))
                {
                    ref var sharedRef = ref Unsafe.As<T, EntitySharedReference>(ref newValue);
                    //skip local ids
                    *(EntitySharedReference*)data = sharedRef.IsLocal ? null : sharedRef;
                }
                else
                    *(T*)data = newValue;
            }
        }

        public void Destroy(ushort serverTick, bool instantly)
        {
            if (_state != SerializerState.Active)
                return;
            if (instantly || serverTick == _versionChangedTick)
            {
                _state = SerializerState.Freed;
                return;
            }
            _state = SerializerState.Destroyed;
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
            CleanPendingRpcs(ref _syncRpcHead, out _syncRpcTail);
            _rpcMode = RPCMode.Sync;
            _entity.OnSyncRequested();
            var syncableFields = _entity.GetClassData().SyncableFields;
            for (int i = 0; i < syncableFields.Length; i++)
                RefMagic.RefFieldValue<SyncableField>(_entity, syncableFields[i].Offset)
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

        public unsafe void MakeBaseline(byte playerId, ushort serverTick, byte* resultData, ref int position)
        {
            //skip inactive and other controlled controllers
            bool isOwned = _entity.InternalOwnerId.Value == playerId;
            if (_state != SerializerState.Active || (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && !isOwned))
                return;
            //don't write total size in full sync and fields
            WriteInitialState(isOwned, serverTick, resultData, ref position);
            //Logger.Log($"[SEM] SendBaseline for entity: {_entity.Id}, pos: {position}, posAfterData: {position + _fullDataSize}");
        }

        public unsafe DiffResult MakeDiff(byte playerId, ushort serverTick, ushort minimalTick, ushort playerTick, byte* resultData, ref int position)
        {
            switch (_state)
            {
                case SerializerState.Freed:
                    return DiffResult.Skip;
                
                case SerializerState.Destroyed when Utils.SequenceDiff(LastChangedTick, minimalTick) < 0:
                    _state = SerializerState.Freed;
                    return DiffResult.DoneAndDestroy;
                
                case SerializerState.Active:
                    if (Utils.SequenceDiff(_versionChangedTick, minimalTick) < 0)
                        _versionChangedTick = minimalTick;
                    break;
            }

            //make diff
            bool isOwned = _entity.InternalOwnerId.Value == playerId;
            if (_flags.HasFlagFast(EntityFlags.OnlyForOwner) && !isOwned)
            {
                return DiffResult.Skip;
            }

            int startPos = position;
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