using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace LiteEntitySystem.Internal
{
    internal struct RemoteCallsCache
    {
        public readonly RPCHeader Header;
        public readonly EntitySharedReference EntityId;
        public readonly int Offset;
        public readonly int SyncableOffset;
        public readonly MethodCallDelegate Delegate;
        public bool Executed;

        public RemoteCallsCache(RPCHeader header, EntitySharedReference entityId, RpcFieldInfo rpcFieldInfo, int offset)
        {
            Header = header;
            EntityId = entityId;
            Delegate = rpcFieldInfo.Method;
            Offset = offset;
            SyncableOffset = rpcFieldInfo.SyncableOffset;
            Executed = false;
            if (Delegate == null)
                Logger.LogError($"ZeroRPC: {header.Id}");
        }
    }

    internal readonly struct InterpolatedCache
    {
        public readonly InternalEntity Entity;
        public readonly int FieldOffset;
        public readonly int FieldFixedOffset;
        public readonly ValueTypeProcessor TypeProcessor;
        public readonly int StateReaderOffset;

        public InterpolatedCache(InternalEntity entity, ref EntityFieldInfo field, int offset)
        {
            Entity = entity;
            FieldOffset = field.Offset;
            FieldFixedOffset = field.FixedOffset;
            TypeProcessor = field.TypeProcessor;
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
        public byte BufferedInputsCount;
        public int InterpolatedCachesCount;
        public InterpolatedCache[] InterpolatedCaches = new InterpolatedCache[32];
        
        private int _totalPartsCount;
        private int _syncableRemoteCallsCount;
        private int _remoteCallsCount;
        private RemoteCallsCache[] _syncableRemoteCallsCaches = new RemoteCallsCache[32];
        private RemoteCallsCache[] _remoteCallsCaches = new RemoteCallsCache[32];
        private int _receivedPartsCount;
        private byte _maxReceivedPart;
        private ushort _partMtu;
        private readonly BitArray _receivedParts = new (EntityManager.MaxParts);
        
        private static readonly ThreadLocal<HashSet<SyncableField>> SyncablesSet = new(()=>new HashSet<SyncableField>());

        public unsafe void Preload(InternalEntity[] entityDict)
        {
            for (int bytesRead = 0; bytesRead < Size;)
            {
                int initialReaderPosition = bytesRead;
                ushort fullSyncAndTotalSize = BitConverter.ToUInt16(Data, initialReaderPosition);
                bool fullSync = (fullSyncAndTotalSize & 1) == 1;
                int totalSize = fullSyncAndTotalSize >> 1;
                bytesRead += totalSize;
                ushort entityId = BitConverter.ToUInt16(Data, initialReaderPosition + sizeof(ushort));
                if (entityId == EntityManager.InvalidEntityId || entityId >= EntityManager.MaxSyncedEntityCount)
                {
                    //Should remove at all
                    Logger.LogError($"[CEM] Invalid entity id: {entityId}");
                    return;
                }
      
                //it should be here at preload
                var entity = entityDict[entityId];
                if (entity == null)
                {
                    //Removed entity
                    //Logger.LogError($"Preload entity: {preloadData.EntityId} == null");
                    continue;
                }

                ref var classData = ref entity.ClassData;
                int entityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                int stateReaderOffset = fullSync 
                    ? initialReaderPosition + StateSerializer.HeaderSize + sizeof(ushort) 
                    : entityFieldsOffset + classData.FieldsFlagsSize;

                //preload interpolation info
                if (entity.IsRemoteControlled && classData.InterpolatedCount > 0)
                    Utils.ResizeIfFull(ref InterpolatedCaches, InterpolatedCachesCount + classData.InterpolatedCount);
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(Data, entityFieldsOffset, i))
                        continue;
                    ref var field = ref classData.Fields[i];
                    if (entity.IsRemoteControlled && field.Flags.HasFlagFast(SyncFlags.Interpolated))
                        InterpolatedCaches[InterpolatedCachesCount++] = new InterpolatedCache(entity, ref field, stateReaderOffset);
                    stateReaderOffset += field.IntSize;
                }

                //preload rpcs
                fixed(byte* rawData = Data)
                    ReadRPCs(rawData, ref stateReaderOffset, new EntitySharedReference(entity.Id, entity.Version), classData);

                if (stateReaderOffset != initialReaderPosition + totalSize)
                {
                    Logger.LogError($"Missread! {stateReaderOffset} > {initialReaderPosition + totalSize}");
                    return;
                }
            }
        }
        
        public void ExecuteSyncableRpcs(ClientEntityManager entityManager, ushort minimalTick, bool firstSync)
        {
            entityManager.IsExecutingRPC = true;
            var syncSet = SyncablesSet.Value;
            syncSet.Clear();
            for (int i = 0; i < _syncableRemoteCallsCount; i++)
            {
                ref var rpc = ref _syncableRemoteCallsCaches[i];
                if (rpc.Executed)
                    continue;
                if (!firstSync && Utils.SequenceDiff(rpc.Header.Tick, minimalTick) <= 0)
                {
                    //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} <= MinimalTick: {minimalTick}. Id: {rpc.Header.Id}.");
                    continue;
                }
                //Logger.Log($"Executing rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}. Type: {rpcType}");
                var entity = entityManager.GetEntityById<InternalEntity>(rpc.EntityId);
                if (entity == null)
                {
                    Logger.Log($"Entity is null: {rpc.EntityId}");
                    continue;
                }
                rpc.Executed = true;
                entityManager.CurrentRPCTick = rpc.Header.Tick;
                var syncableField = RefMagic.RefFieldValue<SyncableField>(entity, rpc.SyncableOffset);
                if (syncSet.Add(syncableField))
                    syncableField.BeforeReadRPC();
                try
                {
                    rpc.Delegate(syncableField, new ReadOnlySpan<byte>(Data, rpc.Offset, rpc.Header.ByteCount));
                }
                catch (Exception e)
                {
                    Logger.LogError($"Error when executing syncableRPC: {entity}. RPCID: {rpc.Header.Id}. {e}");
                }
            }
            foreach (var syncableField in syncSet)
                syncableField.AfterReadRPC();
            entityManager.IsExecutingRPC = false;
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
                    if (Utils.SequenceDiff(rpc.Header.Tick, entityManager.ServerTick) > 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} > ServerTick: {entityManager.ServerTick}. Id: {rpc.Header.Id}.");
                        continue;
                    }
                    if (Utils.SequenceDiff(rpc.Header.Tick, minimalTick) <= 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick} <= MinimalTick: {minimalTick}. Id: {rpc.Header.Id}.");
                        continue;
                    }
                }
                //Logger.Log($"Executing rpc. Entity: {rpc.EntityId}. Tick {rpc.Header.Tick}. Id: {rpc.Header.Id}. Type: {rpcType}");
                var entity = entityManager.GetEntityById<InternalEntity>(rpc.EntityId);
                if (entity == null)
                {
                    Logger.Log($"Entity is null: {rpc.EntityId}");
                    continue;
                }
                rpc.Executed = true;
                entityManager.CurrentRPCTick = rpc.Header.Tick;
                try
                {
                    rpc.Delegate(entity, new ReadOnlySpan<byte>(Data, rpc.Offset, rpc.Header.ByteCount));
                }
                catch (Exception e)
                {
                    Logger.LogError($"Error when executing RPC: {entity}. RPCID: {rpc.Header.Id}. {e}");
                }
            }
            entityManager.IsExecutingRPC = false;
        }

        public unsafe void ReadRPCs(byte* rawData, ref int position, EntitySharedReference entityId, EntityClassData classData)
        {
            int readCount = *(ushort*)(rawData + position);
            //if(readCount > 0)
            //    Logger.Log($"[CEM] ReadRPC Entity: {entityId.Id} Count: {readCount} posAfterData: {position}");
            position += sizeof(ushort);
            Utils.ResizeOrCreate(ref _remoteCallsCaches, _remoteCallsCount + readCount);
            Utils.ResizeOrCreate(ref _syncableRemoteCallsCaches, _syncableRemoteCallsCount + readCount);
            for (int i = 0; i < readCount; i++)
            {
                var header = *(RPCHeader*)(rawData + position);
                if (header.Id >= classData.RemoteCallsClient.Length)
                {
                    Logger.LogError($"BrokenRPC at position: {position}, entityId: {entityId}, classId: {classData.ClassId}");
                    return;
                }
                position += sizeof(RPCHeader);
                var rpcCache = new RemoteCallsCache(header, entityId, classData.RemoteCallsClient[header.Id], position);
                //Logger.Log($"[CEM] ReadRPC. RpcId: {header.Id}, Tick: {header.Tick}, TypeSize: {header.TypeSize}, Count: {header.Count}");

                //this is entity rpc
                if (rpcCache.SyncableOffset == -1)
                {
                    _remoteCallsCaches[_remoteCallsCount] = rpcCache;
                    _remoteCallsCount++;
                }
                else
                {
                    _syncableRemoteCallsCaches[_syncableRemoteCallsCount] = rpcCache;
                    _syncableRemoteCallsCount++;
                }
     
                position += header.ByteCount;
            }
        }

        public void Reset(ushort tick)
        {
            Tick = tick;
            _receivedParts.SetAll(false);
            InterpolatedCachesCount = 0;
            _maxReceivedPart = 0;
            _receivedPartsCount = 0;
            _totalPartsCount = 0;
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
                _totalPartsCount = partHeader.Part + 1;
                _partMtu = (ushort)(lastPartData.Mtu - sizeof(DiffPartHeader));
                LastReceivedTick = lastPartData.LastReceivedTick;
                ProcessedTick = lastPartData.LastProcessedTick;
                BufferedInputsCount = lastPartData.BufferedInputsCount;
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
            _maxReceivedPart = partHeader.Part > _maxReceivedPart ? partHeader.Part : _maxReceivedPart;
            return _receivedPartsCount == _totalPartsCount;
        }
    }
}