using System;
using System.Collections;
using System.Collections.Generic;
using K4os.Compression.LZ4;

namespace LiteEntitySystem.Internal
{
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

    internal readonly struct EntityDataCache
    {
        public readonly ushort EntityId;
        public readonly int Offset;

        public EntityDataCache(ushort entityId, int offset)
        {
            EntityId = entityId;
            Offset = offset;
        }
    }

    internal class ServerStateData
    {
        private const int DefaultCacheSize = 32;
        
        public byte[] Data = new byte[1500];
        public int Size;
        public ushort Tick;
        public ushort ProcessedTick;
        public ushort LastReceivedTick;
        public byte BufferedInputsCount;
        
        private int _totalPartsCount;
        private int _receivedPartsCount;
        private byte _maxReceivedPart;
        private ushort _partMtu;
        private readonly BitArray _receivedParts = new (EntityManager.MaxParts);
        
        private int _dataOffset;
        private int _dataSize;
        private int _rpcReadPos;
        private int _rpcEndPos;

        private EntityDataCache[] _nullEntitiesData = new EntityDataCache[DefaultCacheSize];
        private int _nullEntitiesCount;
        
        public int DataOffset => _dataOffset;
        public int DataSize => _dataSize;
        
        private readonly HashSet<SyncableFieldCustomRollback> _syncablesSet = new();
        private DeltaCompressor _rpcDeltaCompressor = new (Utils.SizeOfStruct<RPCHeader>());

        public ServerStateData() =>
            _rpcDeltaCompressor.Init();
        
        public unsafe void GetDiagnosticData(
            InternalEntity[] entityDict, 
            EntityClassData[] classDatas,
            Dictionary<int, LESDiagnosticDataEntry> diagnosticDataDict)
        {
            fixed (byte* rawData = Data)
            {
                int readPos = 0;
                while (readPos < _rpcEndPos)
                {
                    if (_rpcEndPos - readPos < sizeof(RPCHeader))
                        break;
                    var header = *(RPCHeader*)(rawData + readPos);
                    int rpcSize = header.ByteCount + sizeof(RPCHeader);
                    readPos += rpcSize;

                    int dictId = ushort.MaxValue + header.Id;
                    
                    if(!diagnosticDataDict.TryGetValue(dictId, out LESDiagnosticDataEntry entry))
                    {
                        entry = new LESDiagnosticDataEntry { IsRPC = true, Count = 1, Name = $"{header.Id}", Size = rpcSize };
                    }
                    else
                    {
                        entry.Count++;
                        entry.Size += rpcSize;
                    }
                    diagnosticDataDict[dictId] = entry;
                }
            }

            for (int bytesRead = _dataOffset; bytesRead < _dataOffset + _dataSize;)
            {
                int initialReaderPosition = bytesRead;
                int totalSize = BitConverter.ToUInt16(Data, initialReaderPosition);
                bytesRead += totalSize;
                ushort entityId = BitConverter.ToUInt16(Data, initialReaderPosition + sizeof(ushort));
                int classId = entityDict[entityId] != null 
                    ? entityDict[entityId].ClassId
                    : -1;
                string name = classId >= 0 
                    ? classDatas[classId].ClassEnumName 
                    : "Unknown";
                
                if(!diagnosticDataDict.TryGetValue(classId, out LESDiagnosticDataEntry entry))
                {
                    entry = new LESDiagnosticDataEntry { Count = 1, Name = name, Size = totalSize };
                }
                else
                {
                    entry.Count++;
                    entry.Size += totalSize;
                }
                diagnosticDataDict[classId] = entry;
            }
        }
        
        public void Preload(InternalEntity[] entityDict)
        {
            for (int bytesRead = _dataOffset; bytesRead < _dataOffset + _dataSize;)
            {
                int initialReaderPosition = bytesRead;
                int totalSize = BitConverter.ToUInt16(Data, initialReaderPosition);
                bytesRead += totalSize;
                ushort entityId = BitConverter.ToUInt16(Data, initialReaderPosition + sizeof(ushort));
                if (entityId == EntityManager.InvalidEntityId || entityId >= EntityManager.MaxSyncedEntityCount)
                {
                    //Should remove at all
                    Logger.LogError($"[CEM] Invalid entity id: {entityId}");
                    return;
                }
                
                var entity = entityDict[entityId];
                if (entity == null)
                {
                    Utils.ResizeIfFull(ref _nullEntitiesData, _nullEntitiesCount);
                   _nullEntitiesData[_nullEntitiesCount++] = new EntityDataCache(entityId, initialReaderPosition);
                   //Logger.Log($"Add to pending: {entityId}");
                    continue;
                }
            
                PreloadInterpolation(entity, initialReaderPosition);
            }
        }

        private unsafe void PreloadInterpolation(InternalEntity entity, int offset)
        {
            if (!entity.IsRemoteControlled)
                return;
            
            ref var classData = ref entity.ClassData;
            if (classData.InterpolatedCount == 0)
                return;
            
            int entityFieldsOffset = offset + StateSerializer.DiffHeaderSize;
            int stateReaderOffset = entityFieldsOffset + classData.FieldsFlagsSize;
            
            //interpolated fields goes first so can skip some checks
            fixed (byte* rawData = Data)
            {
                for (int i = 0; i < classData.InterpolatedCount; i++)
                {
                    if (!Utils.IsBitSet(Data, entityFieldsOffset, i))
                        continue;
                    ref var field = ref classData.Fields[i];
                    field.TypeProcessor.SetInterpValue(entity, field.Offset, rawData + stateReaderOffset);
                    stateReaderOffset += field.IntSize;
                }
            }
        }

        public void PreloadInterpolationForNewEntities(InternalEntity[] entityDict)
        {
            for (int i = 0; i < _nullEntitiesCount; i++)
            {
                var entity = entityDict[_nullEntitiesData[i].EntityId];
                if (entity == null) 
                    continue;
                
                //Logger.Log($"Read pending interpolation: {entity.Id}");
                    
                PreloadInterpolation(entity, _nullEntitiesData[i].Offset);
                    
                //remove
                _nullEntitiesCount--;
                _nullEntitiesData[i] = _nullEntitiesData[_nullEntitiesCount];
                i--;
            }
        }
        
        public unsafe void ExecuteRpcs(ClientEntityManager entityManager, ushort minimalTick, bool firstSync)
        {
            _syncablesSet.Clear();
            //if(_remoteCallsCount > 0)
            //    Logger.Log($"Executing rpcs (ST: {Tick}) for tick: {entityManager.ServerTick}, Min: {minimalTick}, Count: {_remoteCallsCount}");
            fixed (byte* rawData = Data)
            {
                while (_rpcReadPos < _rpcEndPos)
                {
                    if (_rpcEndPos - _rpcReadPos < _rpcDeltaCompressor.MinDeltaSize)
                    {
                        Logger.LogError("Broken rpcs sizes?");
                        break;
                    }
                    
                    RPCHeader header = new();
                    int encodedSize = _rpcDeltaCompressor.Decode(
                        new ReadOnlySpan<byte>(rawData + _rpcReadPos, _rpcDeltaCompressor.MaxDeltaSize), 
                        new Span<byte>(&header, sizeof(RPCHeader)));
                    
                    if (!firstSync)
                    {
                        if (Utils.SequenceDiff(header.Tick, entityManager.ServerTick) > 0)
                        {
                            //Logger.Log($"Skip rpc. Entity: {header.EntityId}. Tick {header.Tick} > ServerTick: {entityManager.ServerTick}. Id: {header.Id}.");
                            break;
                        }

                        if (Utils.SequenceDiff(header.Tick, minimalTick) <= 0)
                        {
                            _rpcReadPos += header.ByteCount + encodedSize;
                            //Logger.Log($"Skip rpc. Entity: {header.EntityId}. Tick {header.Tick} <= MinimalTick: {minimalTick}. Id: {header.Id}. StateATick: {entityManager.RawServerTick}. StateBTick: {entityManager.RawTargetServerTick}");
                            continue;
                        }
                    }
                    
                    int rpcDataStart = _rpcReadPos + encodedSize;
                    _rpcReadPos += encodedSize + header.ByteCount;

                    //Logger.Log($"Executing rpc. Entity: {header.EntityId}. Tick {header.Tick}. Id: {header.Id}");
                    var entity = entityManager.EntitiesDict[header.EntityId];
                    if (entity == null)
                    {
                        if (header.Id == RemoteCallPacket.NewRPCId)
                        {
                            entityManager.ReadNewRPC(header.EntityId, rawData + rpcDataStart);
                            continue;
                        }
   
                        Logger.LogError($"Entity is null: {header.EntityId}. RPCId: {header.Id}");
                        continue;
                    }
                    
                    entityManager.CurrentRPCTick = header.Tick;
                    
                    var rpcFieldInfo = entityManager.ClassDataDict[entity.ClassId].RemoteCallsClient[header.Id];
                    if (rpcFieldInfo.SyncableOffset == -1)
                    {
                        try
                        {
                            if (header.Id == RemoteCallPacket.NewRPCId)
                            {
                                entityManager.ReadNewRPC(header.EntityId, rawData + rpcDataStart);
                            }
                            else if (header.Id == RemoteCallPacket.ConstructRPCId)
                            {
                                //Logger.Log($"ConstructRPC for entity: {header.EntityId}, RpcReadPos: {_rpcReadPos}, Tick: {header.Tick}");
                                entityManager.ReadConstructRPC(header.EntityId, rawData, rpcDataStart);
                            }
                            else if (header.Id == RemoteCallPacket.DestroyRPCId)
                            {
                                entity.DestroyInternal();
                            }
                            else
                            {
                                rpcFieldInfo.Method(entity, new ReadOnlySpan<byte>(rawData + rpcDataStart, header.ByteCount));
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Error when executing RPC: {entity}. RPCID: {header.Id}. {e}");
                        }
                    }
                    else
                    {
                        var syncableField = RefMagic.GetFieldValue<SyncableField>(entity, rpcFieldInfo.SyncableOffset);
                        if (syncableField is SyncableFieldCustomRollback sf && _syncablesSet.Add(sf))
                        {
                            sf.BeforeReadRPC();
                        }
                        try
                        {
                            rpcFieldInfo.Method(syncableField, new ReadOnlySpan<byte>(rawData + rpcDataStart, header.ByteCount));
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Error when executing syncableRPC: {entity}. RPCID: {header.Id}. {e}");
                        }
                    }
                }
            }
            foreach (var syncableField in _syncablesSet)
                syncableField.AfterReadRPC();
        }

        public void Reset(ushort tick)
        {
            Tick = tick;
            _receivedParts.SetAll(false);
            _maxReceivedPart = 0;
            _receivedPartsCount = 0;
            _totalPartsCount = 0;
            Size = 0;
            _partMtu = 0;
            _nullEntitiesCount = 0;
            _rpcReadPos = 0;
            _rpcDeltaCompressor.Init();
        }

        public unsafe bool ReadBaseline(BaselineDataHeader header, byte* rawData, int fullSize)
        {
            Reset(header.Tick);
            Size = header.OriginalLength;
            Data = new byte[header.OriginalLength];
            _dataOffset = header.EventsSize;
            _dataSize = header.OriginalLength - header.EventsSize;
            _rpcEndPos = header.EventsSize;
            _rpcReadPos = 0;
            fixed (byte* stateData = Data)
            {
                int decodedBytes = LZ4Codec.Decode(
                    rawData + sizeof(BaselineDataHeader),
                    fullSize - sizeof(BaselineDataHeader),
                    stateData,
                    Size);
                if (decodedBytes != header.OriginalLength)
                {
                    Logger.LogError("Error on decompress");
                    return false;
                }
            }
            return true;
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
                _dataOffset = lastPartData.EventsSize;
                _rpcEndPos = lastPartData.EventsSize;
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

            if (_receivedPartsCount == _totalPartsCount)
            {
                _dataSize = Size - _dataOffset;
                return true;
            }
            return false;
        }
    }
}