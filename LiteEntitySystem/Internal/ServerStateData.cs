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

    internal enum RpcExecutionMode
    {
        FirstSync,
        FastForward,
        Interpolated
    }
    
    internal sealed class ServerStateData
    {
        public byte[] Data = new byte[1500];
        public int Size;
        public ushort Tick;
        public ushort ProcessedTick;
        public ushort LastReceivedTick;
        public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
        public int PreloadDataCount;
        public int[] InterpolatedFields = new int[8];
        public int InterpolatedCount;
        public int RemoteCallsCount;
        public RemoteCallsCache[] RemoteCallsCaches = new RemoteCallsCache[32];
        public ServerDataStatus Status;

        private readonly bool[] _receivedParts = new bool[EntityManager.MaxParts];
        private int _totalPartsCount;
        private int _receivedPartsCount;
        private byte _maxReceivedPart;
        private ushort _partMtu;

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
        
        public void ExecuteRpcs(EntityManager entityManager, ushort serverTick, RpcExecutionMode mode)
        {
            for (int i = 0; i < RemoteCallsCount; i++)
            {
                ref var rpcCache = ref RemoteCallsCaches[i];
                if (rpcCache.Executed)
                    return;
                if (mode != RpcExecutionMode.FirstSync)
                {
                    int sequenceDifference = Utils.SequenceDiff(rpcCache.Header.Tick, serverTick);
                    if (sequenceDifference < 0)
                        return;
                    if (mode == RpcExecutionMode.FastForward && sequenceDifference == 0)
                        return;
                    if (mode == RpcExecutionMode.Interpolated && sequenceDifference > 0)
                        return;
                }
                rpcCache.Executed = true;
                var entity = entityManager.GetEntityById<InternalEntity>(rpcCache.EntityId);
                //Logger.Log($"Executing rpc. Entity: {rpcCache.EntityId}. Tick {rpcCache.Header.Tick}. Id: {rpcCache.Header.Id}");
                var rpcData = new ReadOnlySpan<byte>(Data, rpcCache.Offset, rpcCache.Header.TypeSize * rpcCache.Header.Count);
                if (rpcCache.SyncableOffset == -1)
                {
                    rpcCache.Delegate(entity, rpcData);
                }
                else
                {
                    var syncableField = Utils.RefFieldValue<SyncableField>(entity, rpcCache.SyncableOffset);
                    rpcCache.Delegate(syncableField, rpcData);
                }
            }
        }

        public unsafe void ReadRPCs(byte* rawData, ref int position, EntitySharedReference entityId, EntityClassData classData)
        {
            int prevCount = RemoteCallsCount;
            RemoteCallsCount += *(ushort*)(rawData + position);
            Utils.ResizeOrCreate(ref RemoteCallsCaches, RemoteCallsCount);
            //Logger.Log($"[CEM] ReadRPC Entity: {entityId.Id} Count: {RemoteCallsCount} posAfterData: {position}");
            position += sizeof(ushort);
            for (int i = prevCount; i < RemoteCallsCount; i++)
            {
                var header = *(RPCHeader*)(rawData + position);
                position += sizeof(RPCHeader);
                var rpcCache = new RemoteCallsCache(header, entityId, classData.RemoteCallsClient[header.Id], position, classData.RpcOffsets[header.Id].SyncableOffset);
                //Logger.Log($"[CEM] ReadRPC. RpcId: {header.Id}, Tick: {header.Tick}, TypeSize: {header.TypeSize}, Count: {header.Count}");
                if (rpcCache.Delegate == null)
                {
                    Logger.LogError($"ZeroRPC: {header.Id}");
                }
                RemoteCallsCaches[i] = rpcCache;
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
                RemoteCallsCount = 0;
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