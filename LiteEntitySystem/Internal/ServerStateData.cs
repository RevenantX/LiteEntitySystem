using System;
using System.Collections;
using System.Collections.Generic;
using K4os.Compression.LZ4;

namespace LiteEntitySystem.Internal
{
    internal enum RPCExecuteMode
    {
        FirstSync,
        BetweenStates,
        OnNextState
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

    internal readonly struct RemoteCallInfo
    {
        public readonly RPCHeader Header;
        public readonly int DataOffset;
        public readonly bool ExecuteOnNextState;

        public RemoteCallInfo(RPCHeader header, int dataOffset, bool executeOnNextState)
        {
            Header = header;
            DataOffset = dataOffset;
            ExecuteOnNextState = executeOnNextState;
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

        private EntityDataCache[] _nullEntitiesData = new EntityDataCache[DefaultCacheSize];
        private RemoteCallInfo[] _remoteCallInfos = new RemoteCallInfo[DefaultCacheSize];
        
        private int _nullEntitiesCount;
        private int _remoteCallsCount;
        private int _rpcIndex;
        
        public int DataOffset => _dataOffset;
        public int DataSize => _dataSize;
        
        [ThreadStatic]
        private static HashSet<SyncableFieldCustomRollback> SyncablesBufferSet;
        
        [ThreadStatic]
        private static HashSet<ushort> LocalEntitiesBuffer;
        
        private DeltaCompressor _rpcDeltaCompressor = new (Utils.SizeOfStruct<RPCHeader>());

        private readonly ClientEntityManager _entityManager;

        public ServerStateData(ClientEntityManager entityManager)
        {
            _entityManager = entityManager;
        }
        
        public unsafe void GetDiagnosticData(Dictionary<int, LESDiagnosticDataEntry> diagnosticDataDict)
        {
            for (int i = 0; i < _remoteCallsCount; i++)
            {
                var header = _remoteCallInfos[i].Header;
                int rpcSize = header.ByteCount + sizeof(RPCHeader);
                int dictId = ushort.MaxValue + header.Id;

                if (!diagnosticDataDict.TryGetValue(dictId, out LESDiagnosticDataEntry entry))
                {
                    entry = new LESDiagnosticDataEntry
                        { IsRPC = true, Count = 1, Name = $"{header.Id}", Size = rpcSize };
                }
                else
                {
                    entry.Count++;
                    entry.Size += rpcSize;
                }

                diagnosticDataDict[dictId] = entry;
            }

            var entityDict = _entityManager.EntitiesDict;
            var classDatas = _entityManager.ClassDataDict;
            
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

        public void PreloadInterpolationForNewEntities()
        {
            for (int i = 0; i < _nullEntitiesCount; i++)
            {
                var entity = _entityManager.EntitiesDict[_nullEntitiesData[i].EntityId];
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
        
        public unsafe void ExecuteRpcs(ushort minimalTick, RPCExecuteMode executeMode)
        {
            if (SyncablesBufferSet == null)
                SyncablesBufferSet = new HashSet<SyncableFieldCustomRollback>();
            else
                SyncablesBufferSet.Clear();
            
            //if(_remoteCallsCount > 0)
            //    Logger.Log($"Executing rpcs (ST: {Tick}) for tick: {entityManager.ServerTick}, Min: {minimalTick}, Count: {_remoteCallsCount}");

            int initialRpcIndex = _rpcIndex;
            if (executeMode == RPCExecuteMode.OnNextState)
                _rpcIndex = 0;
            
            fixed (byte* rawData = Data)
            {
                for(;_rpcIndex < _remoteCallsCount; _rpcIndex++)
                {
                    var remoteCallInfo = _remoteCallInfos[_rpcIndex];
                    var header = remoteCallInfo.Header;
                    
                    if (Utils.SequenceDiff(header.Tick, minimalTick) <= 0)
                    {
                        //Logger.Log($"Skip rpc. Entity: {header.EntityId}. Tick {header.Tick} <= MinimalTick: {minimalTick}. Id: {header.Id}. ExecMode: {executeMode}");
                        continue;
                    }
                    
                    if (executeMode == RPCExecuteMode.BetweenStates)
                    {
                        if (remoteCallInfo.ExecuteOnNextState)
                        {
                            continue;
                        }
                        if (Utils.SequenceDiff(header.Tick, _entityManager.ServerTick) > 0)
                        {
                            //Logger.Log($"Skip rpc. Entity: {header.EntityId}. Tick {header.Tick} > ServerTick: {entityManager.ServerTick}. Id: {header.Id}.");
                            break;
                        }
                    }
                    //skip executed inside interpolation
                    else if (executeMode == RPCExecuteMode.OnNextState && remoteCallInfo.ExecuteOnNextState == false && _rpcIndex < initialRpcIndex)
                    {
                        //Logger.Log($"Skip rpc. Entity: {header.EntityId}. _rpcIndex {_rpcIndex} < initialRpcIndex: {initialRpcIndex}. Id: {header.Id}.");
                        continue;
                    }
                    
                    if (header.Id == RemoteCallPacket.NewRPCId)
                    {
                        _entityManager.ReadNewRPC(header.EntityId, rawData + remoteCallInfo.DataOffset);
                        continue;
                    }
                    
                    //Logger.Log($"Executing rpc. Entity: {header.EntityId}. Tick {header.Tick}. Id: {header.Id}");
                    var entity = _entityManager.EntitiesDict[header.EntityId];
                    if (entity == null)
                    {
                        Logger.LogError($"Entity is null: {header.EntityId}. RPCId: {header.Id}");
                        continue;
                    }
                    
                    _entityManager.CurrentRPCTick = header.Tick;
                    
                    var rpcFieldInfo = _entityManager.ClassDataDict[entity.ClassId].RemoteCallsClient[header.Id];
                    if (rpcFieldInfo.SyncableOffset == -1)
                    {
                        try
                        {
                            switch (header.Id)
                            {
                                case RemoteCallPacket.ConstructOwnedRPCId:
                                case RemoteCallPacket.ConstructRPCId:
                                    //Logger.Log($"ConstructRPC for entity: {header.EntityId}, Size: {header.ByteCount}, RpcReadPos: {remoteCallInfo.DataOffset}, Tick: {header.Tick}");
                                    //Logger.Log($"CRPCData: {Utils.BytesToHexString(new ReadOnlySpan<byte>(rawData + remoteCallInfo.DataOffset, header.ByteCount))}");
                                    _entityManager.ReadConstructRPC(header.EntityId, rawData, remoteCallInfo.DataOffset);
                                    break;
                                
                                case RemoteCallPacket.DestroyRPCId:
                                    //Logger.Log($"DestroyRPC for {header.EntityId}");
                                    entity.DestroyInternal();
                                    break;
                                
                                default:
                                    rpcFieldInfo.Method(entity, new ReadOnlySpan<byte>(rawData + remoteCallInfo.DataOffset, header.ByteCount));
                                    break;
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
                        if (syncableField is SyncableFieldCustomRollback sf && SyncablesBufferSet.Add(sf))
                            sf.BeforeReadRPC();
                        
                        try
                        {
                            rpcFieldInfo.Method(syncableField, new ReadOnlySpan<byte>(rawData + remoteCallInfo.DataOffset, header.ByteCount));
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Error when executing syncableRPC: {entity}. RPCID: {header.Id}. {e}");
                        }
                    }
                }
            }
            
            foreach (var syncableField in SyncablesBufferSet)
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
        }

        private unsafe void PreloadRPCs(int rpcsSize)
        {
            if (LocalEntitiesBuffer == null)
                LocalEntitiesBuffer = new HashSet<ushort>();
            else
                LocalEntitiesBuffer.Clear();
            
            //parse and preload rpc infos
            int rpcReadPos = 0;
            _rpcIndex = 0;
            _remoteCallsCount = 0;
            _rpcDeltaCompressor.Init();
            while (rpcReadPos < rpcsSize)
            {
                if (rpcsSize - rpcReadPos < _rpcDeltaCompressor.MinDeltaSize)
                {
                    Logger.LogError("Broken rpcs sizes?");
                    break;
                }
                
                RPCHeader header = new();
                int encodedHeaderSize = _rpcDeltaCompressor.Decode(
                    new ReadOnlySpan<byte>(Data, rpcReadPos, rpcsSize - rpcReadPos), 
                    new Span<byte>(&header, sizeof(RPCHeader)));
                
                //Logger.Log($"ReadRPC: EID: {header.EntityId}, RPCID: {header.Id}, BC: {header.ByteCount}");

                rpcReadPos += encodedHeaderSize;

                bool executeOnNextState;
                if (LocalEntitiesBuffer.Contains(header.EntityId))
                {
                    executeOnNextState = true;
                }
                else if (header.Id == RemoteCallPacket.ConstructOwnedRPCId || 
                         _entityManager.EntitiesDict[header.EntityId]?.InternalOwnerId.Value == _entityManager.InternalPlayerId)
                {
                    LocalEntitiesBuffer.Add(header.EntityId);
                    executeOnNextState = true;
                }
                else
                {
                    executeOnNextState = false;
                }

                Utils.ResizeIfFull(ref _remoteCallInfos, _remoteCallsCount);
                _remoteCallInfos[_remoteCallsCount++] = new RemoteCallInfo(header, rpcReadPos, executeOnNextState);
                
                rpcReadPos += header.ByteCount;
            }
        }

        public unsafe bool ReadBaseline(BaselineDataHeader header, byte* rawData, int fullSize)
        {
            Reset(header.Tick);
            Size = header.OriginalLength;
            Data = new byte[header.OriginalLength];
            _dataOffset = header.EventsSize;
            _dataSize = header.OriginalLength - header.EventsSize;
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
            PreloadRPCs(header.EventsSize);
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
                //rpc before data - so data offset equals size
                PreloadRPCs(_dataOffset);
                return true;
            }
            return false;
        }
    }
}