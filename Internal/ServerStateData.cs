using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib;

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

    internal readonly struct RemoteCallsCache
    {
        public readonly ushort EntityId;
        public readonly byte FieldId;
        public readonly MethodCallDelegate Delegate;
        public readonly ushort Tick;
        public readonly int Offset;
        public readonly ushort Count;

        public RemoteCallsCache(ushort entityId, byte fieldId, MethodCallDelegate callDelegate, ushort tick, int offset,
            ushort count)
        {
            EntityId = entityId;
            FieldId = fieldId;
            Delegate = callDelegate;
            Tick = tick;
            Offset = offset;
            Count = count;
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
    
    internal sealed class ServerStateComparer : IComparer<ServerStateData>
    {
        public int Compare(ServerStateData x, ServerStateData y)
        {
            return Utils.SequenceDiff(x!.Tick, y!.Tick);
        }
    }
    
    internal sealed class ServerStateData
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

        private readonly NetPacketReader[] _packetReaders = new NetPacketReader[EntityManager.MaxParts];
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
            Size = 0;
            Offset = 0;
        }

        public void Preload(InternalEntity[] entityDict)
        {
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
                preloadData.EntityId = BitConverter.ToUInt16(Data, bytesRead + 2);
                preloadData.InterpolatedCachesCount = 0;
                bytesRead += preloadData.TotalSize;
                if (preloadData.EntityId == EntityManager.InvalidEntityId || preloadData.EntityId >= EntityManager.MaxSyncedEntityCount)
                {
                    //Should remove at all
                    Logger.LogError($"[CEM] Invalid entity id: {preloadData.EntityId}");
                    return;
                }

                InternalEntity entity = entityDict[preloadData.EntityId];
                if (fullSync)
                {
                    preloadData.EntityFieldsOffset = -1;
                    preloadData.DataOffset = initialReaderPosition + 4;
                }
                else
                {
                    //it should be here at preload
                    entity = entityDict[preloadData.EntityId];
                    if (entity == null)
                    {
                        Logger.LogError($"Preload entity: {preloadData.EntityId} == null");
                        return;
                    }
                    
                    preloadData.EntityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                    preloadData.DataOffset =
                        initialReaderPosition +
                        StateSerializer.DiffHeaderSize +
                        entity.GetClassData().FieldsFlagsSize;
                }

                if (entity != null)
                {
                    ref var classData = ref entity.GetClassData();
                    var fields = classData.Fields;
                    
                    int stateReaderOffset = preloadData.DataOffset;
                    if (fullSync)
                        //version(byte) + classId(ushort)
                        stateReaderOffset += 3;
                    
                    //preload interpolation info
                    if (entity.IsServerControlled && classData.InterpolatedCount > 0)
                    {
                        Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                        Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedCount);
                        InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;
                    }
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        if (!fullSync && !Utils.IsBitSet(Data, preloadData.EntityFieldsOffset, i))
                            continue;
                        var field = fields[i];
                        if (entity.IsServerControlled && field.Interpolator != null)
                        {
                            preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                            (
                                i, stateReaderOffset
                            );
                        }
                        stateReaderOffset += field.IntSize;
                    }

                    //in full sync there is no rpcs, only full data
                    if (fullSync)
                        continue;

                    //preload rpcs
                    while(stateReaderOffset < initialReaderPosition + preloadData.TotalSize)
                    {
                        byte rpcId = Data[stateReaderOffset];
                        byte fieldId = Data[stateReaderOffset + 1];
                        ushort size = BitConverter.ToUInt16(Data, stateReaderOffset + 4);
                        
                        var rpcCache = new RemoteCallsCache(
                            preloadData.EntityId,
                            fieldId,
                            fieldId == byte.MaxValue
                                ? classData.RemoteCallsClient[rpcId]
                                : classData.SyncableRemoteCallsClient[rpcId],
                            BitConverter.ToUInt16(Data, stateReaderOffset + 2),
                            stateReaderOffset + 6,
                            1 //TODO: count!!!
                            );
                        if (rpcCache.Delegate == null)
                        {
                            Logger.LogError($"ZeroRPC: {rpcId}, FieldId: {fieldId}");
                        }
                        
                        Utils.ResizeOrCreate(ref RemoteCallsCaches, RemoteCallsCount);
                        RemoteCallsCaches[RemoteCallsCount++] = rpcCache;
                        stateReaderOffset += 6 + size;
                    }

                    if (stateReaderOffset != initialReaderPosition + preloadData.TotalSize)
                    {
                        Logger.LogError($"Missread! {stateReaderOffset} > {initialReaderPosition + preloadData.TotalSize}");
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