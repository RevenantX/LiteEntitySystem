using System;
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

    internal enum ServerDataStatus
    {
        Empty,
        Partial,
        Ready,
        Preloaded,
        Executed
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

        public void Preload(InternalEntity[] entityDict)
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
                preloadData.EntityId = BitConverter.ToUInt16(Data, bytesRead + 2);
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
                InternalEntity entity = entityDict[preloadData.EntityId];
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
                    {
                        preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                        (
                            i, stateReaderOffset
                        );
                    }
                    stateReaderOffset += field.IntSize;
                }

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
            int partDataSize;
            
            if (partHeader.Part == 0)
            {
                //First packet processing
                var firstPartHeader = (FirstPartHeader*)rawData;
                ProcessedTick = firstPartHeader->LastProcessedTick;
                LastReceivedTick = firstPartHeader->LastReceivedTick;
                partDataSize = partSize - sizeof(FirstPartHeader);
                rawData += sizeof(FirstPartHeader);
                
                //for one part packets
                if (partHeader.PacketType == EntityManager.PacketDiffSyncLast)
                {
                    Size = partDataSize;
                    Utils.ResizeIfFull(ref Data, partDataSize);
                    fixed(byte* stateData = Data)
                        Unsafe.CopyBlock(stateData, rawData, (uint)partDataSize);
                    Status = ServerDataStatus.Ready;
                    return;
                }
                if(_partMtu == 0)
                    _partMtu = (ushort)partDataSize;
            }
            else
            {
                partDataSize = partSize - sizeof(DiffPartHeader);
                if (partHeader.PacketType == EntityManager.PacketDiffSyncLast)
                {
                    partDataSize -= sizeof(ushort);
                    _totalPartsCount = partHeader.Part + 1;
                    if (_partMtu == 0)  //read MTU at last packet from end
                    {
                        _partMtu = *(ushort*)(rawData + partSize - sizeof(ushort));
                        Utils.ResizeIfFull(ref Data, _partMtu * _totalPartsCount);
                    }
                    //Debug.Log($"TPC: {partNumber} {serverState.TotalPartsCount}");
                }
                else if (_partMtu == 0)
                {
                    _partMtu = (ushort)partDataSize;
                    Utils.ResizeIfFull(ref Data, _partMtu * (partHeader.Part + 1));
                }
                rawData += sizeof(DiffPartHeader);
            }
            fixed(byte* stateData = Data)
                Unsafe.CopyBlock(stateData + _partMtu * partHeader.Part, rawData, (uint)partDataSize);
            _receivedParts[partHeader.Part] = true;
            Size += partDataSize;
            _receivedPartsCount++;
            _maxReceivedPart = Math.Max(_maxReceivedPart, partHeader.Part);
            if (_receivedPartsCount == _totalPartsCount)
                Status = ServerDataStatus.Ready;
        }
    }
}