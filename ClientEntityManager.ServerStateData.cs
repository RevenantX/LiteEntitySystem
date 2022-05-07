using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public sealed partial class ClientEntityManager
    {
        private struct StatePreloadData
        {
            public ushort EntityId;
            public int EntityFieldsOffset;
            public ushort TotalSize;
            public int DataOffset;
            public int InterpolatedCachesCount;
            public InterpolatedCache[] InterpolatedCaches;
        }

        private struct RemoteCallsCache
        {
            public ushort EntityId;
            public MethodCallDelegate Delegate;
            public ushort Tick;
            public int Offset;
        }

        private struct InterpolatedCache
        {
            public int Field;
            public int StateReaderOffset;
            public int InitialDataOffset;
            public InterpolatorDelegate Interpolator;
        }
        
        private struct ServerStateComparer : IComparer<ServerStateData>
        {
            public int Compare(ServerStateData x, ServerStateData y)
            {
                return SequenceDiff(x!.Tick, y!.Tick);
            }
        }
        
        private class ServerStateData
        {
            public readonly NetDataReader FinalReader = new NetDataReader();

            public ushort Tick;
            public ushort ProcessedTick;
            public bool IsBaseline;
            public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
            public int PreloadDataCount;
            public int[] InterpolatedFields = new int[8];
            public int InterpolatedCount;

            private readonly NetPacketReader[] _packetReaders = new NetPacketReader[MaxParts];
            private readonly NetDataWriter _finalWriter = new NetDataWriter();
            private int _totalPartsCount;
            private int _receivedPartsCount;
            private int _maxReceivedPart;
            
            public int RemoteCallsCount;
            public RemoteCallsCache[] RemoteCallsCaches = new RemoteCallsCache[32];
  
            public void Reset(ushort tick)
            {
                for (int i = 0; i <= _maxReceivedPart; i++)
                {
                    ref var statePart = ref _packetReaders[i];
                    statePart?.Recycle();
                    statePart = null;
                }

                IsBaseline = false;
                Tick = tick;
                InterpolatedCount = 0;
                PreloadDataCount = 0;
                _maxReceivedPart = 0;
                _receivedPartsCount = 0;
                _totalPartsCount = 0;
                RemoteCallsCount = 0;
            }

            public void Preload(ClientEntityManager entityManager)
            {
                byte[] readerData = FinalReader.RawData;
                //preload some data
                while (FinalReader.AvailableBytes > 0)
                {
                    Utils.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
                    ref var preloadData = ref PreloadDataArray[PreloadDataCount++];
                    int initialReaderPosition = FinalReader.Position;
                    
                    ushort fullSyncAndTotalSize = FinalReader.GetUShort();
                    preloadData.TotalSize = (ushort)(fullSyncAndTotalSize >> 1);
                    preloadData.EntityId = FinalReader.GetUShort();
                    preloadData.InterpolatedCachesCount = 0;
                    FinalReader.SetPosition(initialReaderPosition + preloadData.TotalSize);
                    
                    if (preloadData.EntityId > MaxEntityCount)
                    {
                        //Should remove at all
                        Logger.LogError($"[CEM] Invalid entity id: {preloadData.EntityId}");
                        return;
                    }

                    if ((fullSyncAndTotalSize & 1) == 1)
                    {
                        preloadData.EntityFieldsOffset = -1;
                        preloadData.DataOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                    }
                    else
                    {
                        //it should be here at preload
                        var entity = entityManager.EntitiesDict[preloadData.EntityId];
                        var classData = entityManager.ClassDataDict[entity.ClassId];
                        var fields = classData.Fields;
                        preloadData.EntityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                        preloadData.DataOffset = 
                            initialReaderPosition + 
                            StateSerializer.DiffHeaderSize + 
                            classData.FieldsFlagsSize;

                        int stateReaderOffset = preloadData.DataOffset;
                        int initialDataOffset = 0;
                        int fieldIndex = 0;
                        
                        //preload interpolation info
                        if (!entity.IsLocalControlled && classData.InterpolatedMethods != null)
                        {
                            Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                            Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedMethods.Length);
                            InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;
                            
                            for (; fieldIndex < classData.InterpolatedMethods.Length; fieldIndex++)
                            {
                                if (Utils.IsBitSet(readerData, preloadData.EntityFieldsOffset, fieldIndex))
                                {
                                    preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                                    {
                                        Field = fieldIndex,
                                        Interpolator = classData.InterpolatedMethods[fieldIndex],
                                        StateReaderOffset = stateReaderOffset,
                                        InitialDataOffset = initialDataOffset
                                    };
                                    stateReaderOffset += fields[fieldIndex].IntSize;
                                }
                                initialDataOffset += fields[fieldIndex].IntSize;
                            }
                        }
                        
                        for (; fieldIndex < classData.FieldsCount; fieldIndex++)
                        {
                            if (Utils.IsBitSet(readerData, preloadData.EntityFieldsOffset, fieldIndex))
                                stateReaderOffset += fields[fieldIndex].IntSize;
                        }

                        //preload rpcs
                        while(stateReaderOffset < initialReaderPosition + preloadData.TotalSize)
                        {
                            byte rpcId = readerData[stateReaderOffset];
                            var rpcCache = new RemoteCallsCache
                            {
                                EntityId = preloadData.EntityId,
                                Delegate = classData.RemoteCallsClient[rpcId],
                                //FieldId = readerData[stateReaderOffset + 1],
                                Tick = Unsafe.AsRef<ushort>(readerData[stateReaderOffset + 2]),
                                Offset = stateReaderOffset + 6
                            };
                            ushort size = Unsafe.AsRef<ushort>(readerData[stateReaderOffset + 4]);
                            Utils.ResizeOrCreate(ref RemoteCallsCaches, RemoteCallsCount);
                            RemoteCallsCaches[RemoteCallsCount++] = rpcCache;
                            stateReaderOffset += 6 + size;
                        }
                    }
                }
            }

            public bool ReadPart(bool isLastPart, NetPacketReader reader)
            {
                //check processed tick
                byte partNumber = reader.GetByte();
                if (partNumber == 0)
                {
                    ProcessedTick = reader.GetUShort();
                }

                if (isLastPart)
                {
                    _totalPartsCount = partNumber + 1;
                    //Debug.Log($"TPC: {partNumber} {serverState.TotalPartsCount}");
                }
                        
                //duplicate ?
                if (_packetReaders[partNumber] != null)
                {
                    reader.Recycle();
                    return false;
                }
                        
                _packetReaders[partNumber] = reader;
                _receivedPartsCount++;
                _maxReceivedPart = Math.Max(_maxReceivedPart, partNumber);

                if (_receivedPartsCount == _totalPartsCount)
                {
                    _finalWriter.Reset();
                    for (int i = 0; i < _totalPartsCount; i++)
                    {
                        ref var statePart = ref _packetReaders[i];
                        _finalWriter.Put(statePart.RawData, statePart.Position, statePart.AvailableBytes);
                        statePart.Recycle();
                        statePart = null;
                    }
                    FinalReader.SetSource(_finalWriter);
                    return true;
                }
                return false;
            }
        }
    }
}