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

            _controllerOwner = _entity is ControllerLogic controller
                ? controller.InternalOwnerId
                : EntityManager.ServerPlayerId;
            
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

        public void Destroy(ushort serverTick, ushort minimalTick)
        {
            if (_state != SerializerState.Active)
                return;
            Write((ushort)(serverTick+1), minimalTick);
            _state = SerializerState.Destroyed;
            _ticksOnDestroy = serverTick;
        }

        public int GetMaximumSize()
        {
            int totalSize = (int)_fullDataSize;
            for (int i = 0; i < _classData.SyncableFieldOffsets.Length; i++)
                totalSize += sizeof(int) + Utils.RefFieldValue<SyncableField>(_entity, _classData.SyncableFieldOffsets[i]).GetFullSyncSize();
            return totalSize;
        }

        public unsafe void MakeBaseline(byte playerId, ushort serverTick, ushort minimalTick, byte* resultData, ref int position)
        {
            if (_state != SerializerState.Active)
                return;
            Write(serverTick, minimalTick);
            if (_controllerOwner != EntityManager.ServerPlayerId && playerId != _controllerOwner)
                return;
            
            //make diff
            fixed (byte* lastEntityData = _latestEntityData)
            {
                //initial state with compression
                //don't write total size in full sync and fields
                Unsafe.CopyBlock(resultData + position, lastEntityData, _fullDataSize);
                position += (int)_fullDataSize;
                for (int i = 0; i < _classData.SyncableFieldOffsets.Length; i++)
                {
                    var syncableField = Utils.RefFieldValue<SyncableField>(_entity, _classData.SyncableFieldOffsets[i]);
                    int writeSize = syncableField.GetFullSyncSize();
                    *(int*)(resultData + position) = writeSize;
                    position += sizeof(int);
                    syncableField.FullSyncWrite(_entity.ServerManager, new Span<byte>(resultData + position, writeSize));
                    position += writeSize;
                }
            }
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
                    Unsafe.CopyBlock(resultData + position, lastEntityData, _fullDataSize);
                    position += (int)_fullDataSize;
                    for (int i = 0; i < _classData.SyncableFieldOffsets.Length; i++)
                    {
                        var syncableField = Utils.RefFieldValue<SyncableField>(_entity, _classData.SyncableFieldOffsets[i]);
                        int writeSize = syncableField.GetFullSyncSize();
                        *(int*)(resultData + position) = writeSize;
                        position += sizeof(int);
                        syncableField.FullSyncWrite(_entity.ServerManager, new Span<byte>(resultData + position, writeSize));
                        position += writeSize;
                    }
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
                        ref var field = ref _classData.Fields[i];
                        if (i % 8 == 0)
                        {
                            fields++;
                            *fields = 0;
                        }

                        if ((!field.Flags.HasFlagFast(SyncFlags.OnlyForOwner) || localControlled) &&
                            (!field.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) || !localControlled) &&
                            Utils.SequenceDiff(_fieldChangeTicks[i], playerTick) > 0)
                        {
                            hasChanges = true;
                            *fields |= (byte)(1 << i % 8);
                            Unsafe.CopyBlock(resultData + position, entityDataAfterHeader + field.FixedOffset, field.Size);
                            position += field.IntSize;
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
                            *(ushort*)(resultData + position + 2) = rpcNode.Tick;
                            *(ushort*)(resultData + position + 4) = rpcNode.Size;
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

            return DiffResult.Done;
        }
    }
}