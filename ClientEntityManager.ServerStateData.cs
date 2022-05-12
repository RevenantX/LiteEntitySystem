using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib;
using LiteEntitySystem.Internal;

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
        }
        
        private struct ServerStateComparer : IComparer<ServerStateData>
        {
            public int Compare(ServerStateData x, ServerStateData y)
            {
                return Utils.SequenceDiff(x!.Tick, y!.Tick);
            }
        }
        
        private class ServerStateData
        {
            public byte[] Data;
            public int Size;
            public int Offset;
            
            public ushort Tick;
            public ushort ProcessedTick;
            public ushort LastReceivedTick;
            public bool IsBaseline;
            public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
            public int PreloadDataCount;
            public int[] InterpolatedFields = new int[8];
            public int InterpolatedCount;

            private readonly NetPacketReader[] _packetReaders = new NetPacketReader[MaxParts];
            private int _totalPartsCount;
            private int _receivedPartsCount;
            private int _maxReceivedPart;

            public int RemoteCallsProcessed;
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
                RemoteCallsProcessed = 0;
                Size = 0;
                Offset = 0;
            }

            public void Preload(ClientEntityManager entityManager)
            {
                int bytesRead = 0;
                //preload some data
                while (bytesRead < Size)
                {
                    Utils.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
                    ref var preloadData = ref PreloadDataArray[PreloadDataCount++];
                    ushort fullSyncAndTotalSize = BitConverter.ToUInt16(Data, bytesRead);
                    preloadData.TotalSize = (ushort)(fullSyncAndTotalSize >> 1);
                    preloadData.EntityId = BitConverter.ToUInt16(Data, bytesRead + 2);
                    preloadData.InterpolatedCachesCount = 0;
                    int initialReaderPosition = bytesRead;
                    bytesRead += preloadData.TotalSize;
                    
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
                        //preload interpolation info
                        if (!entity.IsLocalControlled && classData.InterpolatedCount > 0)
                        {
                            Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                            Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedCount);
                            InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;
                        }
                        for (int fieldIndex = 0; fieldIndex < classData.FieldsCount; fieldIndex++)
                        {
                            if (!Utils.IsBitSet(Data, preloadData.EntityFieldsOffset, fieldIndex))
                                continue;
                            var field = fields[fieldIndex];
                            if (!entity.IsLocalControlled && field.Interpolator != null)
                            {
                                preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                                {
                                    Field = fieldIndex,
                                    StateReaderOffset = stateReaderOffset
                                };
                            }
                            stateReaderOffset += field.IntSize;
                        }

                        //preload rpcs
                        while(stateReaderOffset < initialReaderPosition + preloadData.TotalSize)
                        {
                            byte rpcId = Data[stateReaderOffset];
                            var rpcCache = new RemoteCallsCache
                            {
                                EntityId = preloadData.EntityId,
                                Delegate = classData.RemoteCallsClient[rpcId],
                                //FieldId = readerData[stateReaderOffset + 1],
                                Tick = BitConverter.ToUInt16(Data, stateReaderOffset + 2),
                                Offset = stateReaderOffset + 6
                            };
                            ushort size = BitConverter.ToUInt16(Data, stateReaderOffset + 4);
                            Utils.ResizeOrCreate(ref RemoteCallsCaches, RemoteCallsCount);
                            RemoteCallsCaches[RemoteCallsCount++] = rpcCache;
                            stateReaderOffset += 6 + size;
                        }
                    }
                }
            }

            public unsafe bool ReadPart(bool isLastPart, NetPacketReader reader)
            {
                //check processed tick
                byte partNumber = reader.GetByte();
                if (partNumber == 0)
                {
                    ProcessedTick = reader.GetUShort();
                    LastReceivedTick = reader.GetUShort();
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

                Size += reader.AvailableBytes;
                _packetReaders[partNumber] = reader;
                _receivedPartsCount++;
                _maxReceivedPart = Math.Max(_maxReceivedPart, partNumber);

                if (_receivedPartsCount == _totalPartsCount)
                {
                    int writePosition = 0;
                    for (int i = 0; i < _totalPartsCount; i++)
                    {
                        ref var statePart = ref _packetReaders[i];
                        Utils.ResizeOrCreate(ref Data, Size);
                        fixed (byte* data = Data, stateData = statePart.RawData)
                        {
                            Unsafe.CopyBlock(data + writePosition, stateData + statePart.Position, (uint)statePart.AvailableBytes);
                        }
                        writePosition += statePart.AvailableBytes;
                        statePart.Recycle();
                        statePart = null;
                    }
                    return true;
                }
                return false;
            }
        }
    }
}