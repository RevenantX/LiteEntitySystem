using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
        public readonly int SyncableId;
        public readonly MethodCallDelegate Delegate;
        public bool Executed;

        public RemoteCallsCache(RPCHeader header, EntitySharedReference entityId, RpcData rpcData, int offset)
        {
            Header = header;
            EntityId = entityId;
            Delegate = rpcData.ClientMethod;
            Offset = offset;
            SyncableId = rpcData.SyncableId;
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

    internal class ServerStateData
    {
        public byte[] Data = new byte[1500];
        public int Size;
        public ushort Tick;
        public ushort ProcessedTick;
        public ushort LastReceivedTick;
        public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
        public int PreloadDataCount;
        public int[] InterpolatedEntities = new int[8];
        public int InterpolatedEntitiesCount;
        public int TotalPartsCount;

        private int _syncableRemoteCallsCount;
        private RemoteCallsCache[] _syncableRemoteCallCaches = new RemoteCallsCache[32];
        private int _remoteCallsCount;
        private RemoteCallsCache[] _remoteCallsCaches = new RemoteCallsCache[32];
        private readonly bool[] _receivedParts = new bool[EntityManager.MaxParts];
        private int _receivedPartsCount;
        private byte _maxReceivedPart;
        private ushort _partMtu;

        public unsafe void Preload(InternalEntity[] entityDict)
        {
            int bytesRead = 0;
            //preload some data
            while (bytesRead < Size)
            {
                int initialReaderPosition = bytesRead;
                
                Helpers.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
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
                    entity.GetClassMetadata().FieldsFlagsSize;
                
                var classData = entity.GetClassMetadata();
                var fields = classData.Fields;
                int stateReaderOffset = preloadData.DataOffset;

                //preload interpolation info
                if (entity.IsRemoteControlled && classData.InterpolatedCount > 0)
                {
                    Helpers.ResizeIfFull(ref InterpolatedEntities, InterpolatedEntitiesCount);
                    Helpers.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedCount);
                    InterpolatedEntities[InterpolatedEntitiesCount++] = PreloadDataCount - 1;
                }
                
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!Helpers.IsBitSet(Data, preloadData.EntityFieldsOffset, i))
                        continue;
                    var field = fields[i];
                    if (entity.IsRemoteControlled && field.Flags.HasFlagFast(SyncFlags.Interpolated))
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
        
        public void ExecuteSyncFieldRpcs(ClientEntityManager entityManager, ushort minimalTick, bool firstSync)
        {
            //if(_syncableRemoteCallsCount > 0)
            //    Logger.Log($"Executing rpcs (ST: {Tick}) for tick: {entityManager.ServerTick}, Min: {minimalTick}, Count: {_syncableRemoteCallsCount}");
            for (int i = 0; i < _syncableRemoteCallsCount; i++)
            {
                ref var rpc = ref _syncableRemoteCallCaches[i];
                if (rpc.Executed)
                    continue;
                if (!firstSync && Helpers.SequenceDiff(rpc.Header.Tick, minimalTick) <= 0)
                {
                    //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} <= MinimalTick: {minimalTick}. Current: {entityManager.RawServerTick}. Id: {rpc.Header.Id}.");
                    continue;
                }
                var entity = entityManager.GetEntityById<InternalEntity>(rpc.EntityId);
                if (entity == null)
                {
                    Logger.Log($"Entity is null: {rpc.EntityId}");
                    continue;
                }
                //Logger.Log($"Executing rpc. Entity: {rpc.EntityId} Class: {entity.ClassId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}");
                var syncableField = entity.InternalGetSyncableFieldById(rpc.SyncableId);
                rpc.Executed = true;
                rpc.Delegate(syncableField, new ReadOnlySpan<byte>(Data, rpc.Offset, rpc.Header.TypeSize * rpc.Header.Count));
            }
        }
        
        public void ExecuteRpcs(ClientEntityManager entityManager, ushort minimalTick, bool firstSync)
        {
            entityManager.IsExecutingRPC = true;
            //if(_remoteCallsCount > 0)
            //    Logger.Log($"Executing rpcs (ST: {Tick}) for tick: {entityManager.ServerTick}, Min: {minimalTick}, Count: {_remoteCallsCount}");
            for (int i = 0; i < _remoteCallsCount; i++)
            {
                ref var rpc = ref _remoteCallsCaches[i];
                if (rpc.Executed)
                    continue;
                if (!firstSync)
                {
                    if (Helpers.SequenceDiff(rpc.Header.Tick, entityManager.ServerTick) > 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} > ServerTick: {entityManager.ServerTick}. Id: {rpc.Header.Id}.");
                        continue;
                    }
                    if (Helpers.SequenceDiff(rpc.Header.Tick, minimalTick) <= 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} <= MinimalTick: {minimalTick}. Id: {rpc.Header.Id}.");
                        continue;
                    }
                }
                //Logger.Log($"Executing rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}.");
                var entity = entityManager.GetEntityById<InternalEntity>(rpc.EntityId);
                if (entity == null)
                {
                    Logger.Log($"Entity is null: {rpc.EntityId}");
                    continue;
                }
                rpc.Executed = true;
                entityManager.CurrentRPCTick = rpc.Header.Tick;
                rpc.Delegate(entity, new ReadOnlySpan<byte>(Data, rpc.Offset, rpc.Header.TypeSize * rpc.Header.Count));
            }
            entityManager.IsExecutingRPC = false;
        }

        public unsafe void ReadRPCs(byte* rawData, ref int position, EntitySharedReference entityId, GeneratedClassMetadata classData)
        {
            int readCount = *(ushort*)(rawData + position);
            //if(readCount > 0)
            //    Logger.Log($"[CEM] ReadRPC Entity: {entityId.Id} Count: {readCount} posAfterData: {position}");
            position += sizeof(ushort);
            Helpers.ResizeOrCreate(ref _remoteCallsCaches, _remoteCallsCount + readCount);
            Helpers.ResizeOrCreate(ref _syncableRemoteCallCaches, _syncableRemoteCallsCount + readCount);
            for (int i = 0; i < readCount; i++)
            {
                var header = *(RPCHeader*)(rawData + position);
                position += sizeof(RPCHeader);
                var rpcCache = new RemoteCallsCache(header, entityId, classData.RpcData[header.Id], position);
                //Logger.Log($"[CEM] ReadRPC. RpcId: {header.Id}, Tick: {header.Tick}, TypeSize: {header.TypeSize}, Count: {header.Count}");
                if (rpcCache.Delegate == null)
                {
                    Logger.LogError($"ZeroRPC: {header.Id}");
                }

                //this is entity rpc
                if (classData.RpcData[header.Id].SyncableId == -1)
                {
                    _remoteCallsCaches[_remoteCallsCount] = rpcCache;
                    _remoteCallsCount++;
                }
                else
                {
                    _syncableRemoteCallCaches[_syncableRemoteCallsCount] = rpcCache;
                    _syncableRemoteCallsCount++;
                }
     
                position += header.TypeSize * header.Count;
            }
        }

        public void Reset(ushort tick)
        {
            Tick = tick;
            Array.Clear(_receivedParts, 0, _maxReceivedPart+1);
            InterpolatedEntitiesCount = 0;
            PreloadDataCount = 0;
            _maxReceivedPart = 0;
            _receivedPartsCount = 0;
            TotalPartsCount = 0;
            _remoteCallsCount = 0;
            _syncableRemoteCallsCount = 0;
            Size = 0;
            _partMtu = 0;
        }

        public unsafe bool ReadPart(DiffPartHeader partHeader, byte* rawData, int partSize)
        {
            if (_receivedParts[partHeader.Part])
            {
                //duplicate ?
                return false;
            }
            if (partHeader.PacketType == InternalPackets.DiffSyncLast)
            {
                partSize -= sizeof(LastPartData);
                var lastPartData = *(LastPartData*)(rawData + partSize);
                TotalPartsCount = partHeader.Part + 1;
                _partMtu = (ushort)(lastPartData.Mtu - sizeof(DiffPartHeader));
                LastReceivedTick = lastPartData.LastReceivedTick;
                ProcessedTick = lastPartData.LastProcessedTick;
                //Logger.Log($"TPC: {partHeader.Part} {_partMtu}, LastReceivedTick: {LastReceivedTick}, LastProcessedTick: {ProcessedTick}");
            }
            partSize -= sizeof(DiffPartHeader);
            if(_partMtu == 0)
                _partMtu = (ushort)partSize;
            Helpers.ResizeIfFull(ref Data, TotalPartsCount > 1 
                ? _partMtu * TotalPartsCount 
                : _partMtu * partHeader.Part + partSize);
            fixed(byte* stateData = Data)
                Unsafe.CopyBlock(stateData + _partMtu * partHeader.Part, rawData + sizeof(DiffPartHeader), (uint)partSize);
            _receivedParts[partHeader.Part] = true;
            Size += partSize;
            _receivedPartsCount++;
            _maxReceivedPart = Math.Max(_maxReceivedPart, partHeader.Part);
            return _receivedPartsCount == TotalPartsCount;
        }
    }
}