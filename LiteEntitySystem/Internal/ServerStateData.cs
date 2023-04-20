using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    internal struct StatePreloadData
    {
        public ushort EntityId;
        public int EntityFieldsOffset;
        public ushort TotalSize;
        public int DataOffset;
        public int InterpolatedCachesCount;
        public InterpolatedCache[] InterpolatedCaches;
    }

    internal struct RemoteCallsCache
    {
        public readonly RPCHeader Header;
        public readonly EntitySharedReference EntityId;
        public readonly int Offset;
        public readonly int SyncableOffset;
        public readonly MethodCallDelegate Delegate;
        public bool Executed;

        public RemoteCallsCache(RPCHeader header, EntitySharedReference entityId, MethodCallDelegate callDelegate, int offset, int syncableOffset)
        {
            Header = header;
            EntityId = entityId;
            Delegate = callDelegate;
            Offset = offset;
            SyncableOffset = syncableOffset;
            Executed = false;
        }
    }

    internal readonly struct InterpolatedCache
    {
        public readonly int Field;
        public readonly int StateReaderOffset;

        public InterpolatedCache(int fieldId, int offset)
        {
            Field = fieldId;
            StateReaderOffset = offset;
        }
    }

    internal enum ServerDataStatus
    {
        Empty,
        Partial,
        Ready,
        Preloaded,
        Executed
    }

    internal struct ServerStateData
    {
        public byte[] Data;
        public int Size;
        public ushort Tick;
        public ushort ProcessedTick;
        public ushort LastReceivedTick;
        public StatePreloadData[] PreloadDataArray;
        public int PreloadDataCount;
        public int[] InterpolatedFields;
        public int InterpolatedCount;
        public ServerDataStatus Status;

        private int _remoteCallsCount;
        private RemoteCallsCache[] _remoteCallsCaches;
        private bool[] _receivedParts;
        private int _totalPartsCount;
        private int _receivedPartsCount;
        private byte _maxReceivedPart;
        private ushort _partMtu;

        public void Init()
        {
            Data = new byte[1500];
            PreloadDataArray = new StatePreloadData[32];
            InterpolatedFields = new int[8];
            _remoteCallsCaches = new RemoteCallsCache[32];
            _receivedParts = new bool[EntityManager.MaxParts];
        }

        public unsafe void Preload(InternalEntity[] entityDict)
        {
            if (Status != ServerDataStatus.Ready)
            {
                Logger.LogError($"Invalid status on preload: {Status}");
                return;
            }
            Status = ServerDataStatus.Preloaded;
            
            int bytesRead = 0;
            //preload some data
            while (bytesRead < Size)
            {
                int initialReaderPosition = bytesRead;
                
                Utils.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
                ref var preloadData = ref PreloadDataArray[PreloadDataCount++];
                ushort fullSyncAndTotalSize = BitConverter.ToUInt16(Data, bytesRead);

                bool fullSync = (fullSyncAndTotalSize & 1) == 1;
                preloadData.TotalSize = (ushort)(fullSyncAndTotalSize >> 1);
                preloadData.EntityId = BitConverter.ToUInt16(Data, bytesRead + sizeof(ushort));
                preloadData.InterpolatedCachesCount = 0;
                bytesRead += preloadData.TotalSize;
                
                if (preloadData.EntityId > EntityManager.MaxSyncedEntityCount)
                {
                    //Should remove at all
                    Logger.LogError($"[CEM] Invalid entity id: {preloadData.EntityId}");
                    return;
                }
                
                if (fullSync)
                {
                    preloadData.EntityFieldsOffset = -1;
                    preloadData.DataOffset = initialReaderPosition + 4;
                    continue;
                }
      
                //it should be here at preload
                var entity = entityDict[preloadData.EntityId];
                if (entity == null)
                {
                    //Removed entity
                    //Logger.LogError($"Preload entity: {preloadData.EntityId} == null");
                    PreloadDataCount--;
                    continue;
                }

                preloadData.EntityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                preloadData.DataOffset =
                    initialReaderPosition +
                    StateSerializer.DiffHeaderSize +
                    entity.GetClassData().FieldsFlagsSize;
                
                ref var classData = ref entity.GetClassData();
                var fields = classData.Fields;
                int stateReaderOffset = preloadData.DataOffset;

                //preload interpolation info
                if (entity.IsServerControlled && classData.InterpolatedCount > 0)
                {
                    Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                    Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedCount);
                    InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;
                }
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!Utils.IsBitSet(Data, preloadData.EntityFieldsOffset, i))
                        continue;
                    var field = fields[i];
                    if (entity.IsServerControlled && field.Flags.HasFlagFast(SyncFlags.Interpolated))
                        preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache(i, stateReaderOffset);
                    stateReaderOffset += field.IntSize;
                }

                //preload rpcs
                fixed(byte* rawData = Data)
                    ReadRPCs(rawData, ref stateReaderOffset, new EntitySharedReference(entity.Id, entity.Version), classData);

                if (stateReaderOffset != initialReaderPosition + preloadData.TotalSize)
                {
                    Logger.LogError($"Missread! {stateReaderOffset} > {initialReaderPosition + preloadData.TotalSize}");
                }
            }
        }
        
        public void ExecuteRpcs(ClientEntityManager entityManager, ushort minimalTick, bool firstSync)
        {
            //if(_remoteCallsCount > 0)
            //    Logger.Log($"Executing rpcs (ST: {Tick}) for tick: {entityManager.ServerTick}, Min: {minimalTick}, Count: {_remoteCallsCount}");
            for (int i = 0; i < _remoteCallsCount; i++)
            {
                ref var rpc = ref _remoteCallsCaches[i];
                if (rpc.Executed)
                    continue;
                if (!firstSync)
                {
                    if (Utils.SequenceDiff(rpc.Header.Tick, entityManager.ServerTick) > 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} > ServerTick: {entityManager.ServerTick}. Id: {rpc.Header.Id}.");
                        continue;
                    }

                    if (Utils.SequenceDiff(rpc.Header.Tick, minimalTick) <= 0)
                    {
                        //Logger.Log($"Skip rpc (T<=MT). Entity: {rpc.EntityId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}.");
                        continue;
                    }
                }

                rpc.Executed = true;
                //Logger.Log($"Executing rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}.");
                var entity = entityManager.GetEntityById<InternalEntity>(rpc.EntityId);
                var rpcData = new ReadOnlySpan<byte>(Data, rpc.Offset, rpc.Header.TypeSize * rpc.Header.Count);
                if (rpc.SyncableOffset == -1)
                {
                    rpc.Delegate(entity, rpcData);
                }
                else
                {
                    var syncableField = Utils.RefFieldValue<SyncableField>(entity, rpc.SyncableOffset);
                    rpc.Delegate(syncableField, rpcData);
                    entityManager.MarkSyncableFieldChanged(syncableField);
                }
            }
        }

        public unsafe void ReadRPCs(byte* rawData, ref int position, EntitySharedReference entityId, EntityClassData classData)
        {
            int prevCount = _remoteCallsCount;
            _remoteCallsCount += *(ushort*)(rawData + position);
            Utils.ResizeOrCreate(ref _remoteCallsCaches, _remoteCallsCount);
            //Logger.Log($"[CEM] ReadRPC Entity: {entityId.Id} Count: {RemoteCallsCount} posAfterData: {position}");
            position += sizeof(ushort);
            for (int i = prevCount; i < _remoteCallsCount; i++)
            {
                var header = *(RPCHeader*)(rawData + position);
                position += sizeof(RPCHeader);
                var rpcCache = new RemoteCallsCache(header, entityId, classData.RemoteCallsClient[header.Id], position, classData.RpcOffsets[header.Id].SyncableOffset);
                //Logger.Log($"[CEM] ReadRPC. RpcId: {header.Id}, Tick: {header.Tick}, TypeSize: {header.TypeSize}, Count: {header.Count}");
                if (rpcCache.Delegate == null)
                {
                    Logger.LogError($"ZeroRPC: {header.Id}");
                }
                _remoteCallsCaches[i] = rpcCache;
                position += header.TypeSize * header.Count;
            }
        }

        public unsafe void ReadPart(DiffPartHeader partHeader, byte* rawData, int partSize)
        {
            //reset if not same
            if (Tick != partHeader.Tick)
            {
                Tick = partHeader.Tick;
                Array.Clear(_receivedParts, 0, _maxReceivedPart+1);
                InterpolatedCount = 0;
                PreloadDataCount = 0;
                _maxReceivedPart = 0;
                _receivedPartsCount = 0;
                _totalPartsCount = 0;
                _remoteCallsCount = 0;
                Size = 0;
                _partMtu = 0;
            }
            else if (_receivedParts[partHeader.Part])
            {
                //duplicate ?
                return;
            }
            Status = ServerDataStatus.Partial;
            if (partHeader.PacketType == InternalPackets.DiffSyncLast)
            {
                partSize -= sizeof(LastPartData);
                var lastPartData = *(LastPartData*)(rawData + partSize);
                _totalPartsCount = partHeader.Part + 1;
                _partMtu = (ushort)(lastPartData.Mtu - sizeof(DiffPartHeader));
                LastReceivedTick = lastPartData.LastReceivedTick;
                ProcessedTick = lastPartData.LastProcessedTick;
                //Logger.Log($"TPC: {partHeader.Part} {_partMtu}, LastReceivedTick: {LastReceivedTick}, LastProcessedTick: {ProcessedTick}");
            }
            partSize -= sizeof(DiffPartHeader);
            if(_partMtu == 0)
                _partMtu = (ushort)partSize;
            Utils.ResizeIfFull(ref Data, _totalPartsCount > 1 
                ? _partMtu * _totalPartsCount 
                : _partMtu * partHeader.Part + partSize);
            fixed(byte* stateData = Data)
                RefMagic.CopyBlock(stateData + _partMtu * partHeader.Part, rawData + sizeof(DiffPartHeader), (uint)partSize);
            _receivedParts[partHeader.Part] = true;
            Size += partSize;
            _receivedPartsCount++;
            _maxReceivedPart = Math.Max(_maxReceivedPart, partHeader.Part);
            if (_receivedPartsCount == _totalPartsCount)
                Status = ServerDataStatus.Ready;
        }
    }
}