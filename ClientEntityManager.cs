using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public interface IInputGenerator
    {
        void GenerateInput(NetDataWriter writer);
    }

    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed class ClientEntityManager : EntityManager
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
            
            private readonly NetPacketReader[] _packetReaders = new NetPacketReader[MaxParts];
            private readonly NetDataWriter _finalWriter = new NetDataWriter();
            private int _totalPartsCount;
            private int _receivedPartsCount;
            private int _maxReceivedPart;

            public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
            public int PreloadDataCount;
            public int[] InterpolatedFields = new int[8];
            public int InterpolatedCount;
  
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
            }

            public void Preload(ClientEntityManager entityManager)
            {
                //preload some data
                while (FinalReader.AvailableBytes > 0)
                {
                    Utils.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
                    ref var preloadData = ref PreloadDataArray[PreloadDataCount++];
                    int initialReaderPosition = FinalReader.Position;
                    
                    ushort fullSyncAndTotalSize = FinalReader.GetUShort();
                    preloadData.TotalSize = (ushort)(fullSyncAndTotalSize >> 1);
                    preloadData.EntityId = FinalReader.GetUShort();
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
                        //it should be herer at preload
                        var entity = entityManager.EntitiesArray[preloadData.EntityId];
                        var classData = entityManager.ClassDataDict[entity.ClassId];
                        preloadData.EntityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                        preloadData.DataOffset = 
                            initialReaderPosition + 
                            StateSerializer.DiffHeaderSize + 
                            classData.FieldsFlagsSize;
                        preloadData.InterpolatedCachesCount = 0;
                        
                        //preload interpolation info
                        if (entity.IsLocalControlled || classData.InterpolatedMethods == null)
                        {
                            return;
                        }

                        int stateReaderOffset = preloadData.DataOffset;
                        int initialDataOffset = 0;

                        Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                        Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedMethods.Length);
                        InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;

                        for (int i = 0; i < classData.InterpolatedMethods.Length; i++)
                        {
                            if (preloadData.EntityFieldsOffset == -1 ||
                                (FinalReader.RawData[preloadData.EntityFieldsOffset + i/8] & (1 << i%8)) != 0)
                            {
                                preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                                {
                                    Field = i,
                                    Interpolator = classData.InterpolatedMethods[i],
                                    StateReaderOffset = stateReaderOffset,
                                    InitialDataOffset = initialDataOffset
                                };
                                stateReaderOffset += classData.Fields[i].IntSize;
                            }
                            initialDataOffset += classData.Fields[i].IntSize;
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

        private readonly NetPeer _localPeer;
        private readonly NetDataWriter _netDataWriter = new NetDataWriter();
        
        private readonly SortedList<ushort, ServerStateData> _receivedStates = new SortedList<ushort, ServerStateData>();
        private readonly Queue<ServerStateData> _statesPool = new Queue<ServerStateData>(MaxSavedStateDiff);

        public override byte PlayerId => (byte)(_localPeer.RemoteId + 1);

        public int StoredCommands => _inputCommands.Count;
        public int LastProcessedTick => _lastProcessedTick;
        public int LerpBufferCount => _lerpBuffer.Count;

        private bool _isSyncReceived;
        private int _lastProcessedTick;
        
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly LiteRingBuffer<NetDataWriter> _inputCommands = new LiteRingBuffer<NetDataWriter>(32);

        private readonly IInputGenerator _inputGenerator;
        
        private const int InterpolateBufferSize = 10;
        private readonly SortedSet<ServerStateData> _lerpBuffer = new SortedSet<ServerStateData>(new ServerStateComparer());
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        
        private readonly StateSerializer[] _predictedEntities = new StateSerializer[MaxEntityCount];
        private readonly EntityFilter<InternalEntity> _ownedEntities = new EntityFilter<InternalEntity>();
        public bool PredictionReset { get; private set; }
        private readonly NetDataWriter _predictWriter = new NetDataWriter(false, NetConstants.MaxPacketSize*MaxParts);
        private readonly NetDataReader _predictReader = new NetDataReader();
        
        private int _fieldsIndex;
        private NetDataReader _currentReader;

        public ClientEntityManager(NetPeer localPeer, byte headerByte, int framesPerSecond, IInputGenerator inputGenerator) : base(NetworkMode.Client, framesPerSecond)
        {
            _localPeer = localPeer;
            _netDataWriter.Put(headerByte);
            _inputCommands.Fill(() =>
            {
                var writer = new NetDataWriter();
                writer.Put(headerByte);
                writer.Put(PacketClientSync);
                return writer;
            });
            _inputGenerator = inputGenerator;
        }

        protected override void OnLogicTick()
        {
            if (_inputCommands.IsFull)
            {
                _inputCommands.RemoveFromStart(1);
            }
            
            var inputWriter = _inputCommands.Add();
            inputWriter.SetPosition(2);
            inputWriter.Put(ServerTick);
            inputWriter.Put(Tick);
            
            foreach(var controller in GetEntities<HumanControllerLogic>())
            {
                int sizeBefore = inputWriter.Length;
                _inputGenerator.GenerateInput(inputWriter);
                _inputReader.SetSource(inputWriter.Data, sizeBefore, inputWriter.Length);
                controller.ReadInput(_inputReader);
                _inputReader.Clear();
            }
            
            foreach (var entity in _ownedEntities)
            {
                unsafe
                {
                    //save data for interpolation before update
                    var entityLocal = entity;
                    var classData = ClassDataDict[entity.ClassId];
                    int offset = 0;
      
                    byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref entityLocal);
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous value
                        for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                        {
                            var field = classData.Fields[i];
                            Unsafe.CopyBlock(entityPtr + field.Offset, currentDataPtr + offset, field.Size);
                            offset += field.IntSize;
                        }
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                                            
                        //update
                        entity.Update();
                    
                        //save current
                        offset = 0;
                        for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                        {
                            var field = classData.Fields[i];
                            Unsafe.CopyBlock(currentDataPtr + offset, entityPtr + field.Offset, field.Size);
                            offset += field.IntSize;
                        }
                    }
                }
            }
        }

        public override void Update()
        {
            CheckStart();

            if (!_isSyncReceived)
                return;

            base.Update();
            
            //local interpolation
            float localLerpT = (float)(_accumulator/DeltaTime);
            foreach (var entity in _ownedEntities)
            {
                var entityLocal = entity;
                var classData = ClassDataDict[entity.ClassId];
                int offset = 0;
                
                unsafe
                {
                    byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref entityLocal);
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                        {
                            var field = classData.Fields[i];
                            classData.InterpolatedMethods[i](
                                prevDataPtr + offset,
                                currentDataPtr + offset,
                                entityPtr + field.Offset,
                                localLerpT);
                            offset += field.IntSize;
                        }
                    }
                }
            }
            
            if (_stateB == null)
            {
                if (_lerpBuffer.Count > 1)
                {
                    _stateB = _lerpBuffer.Min;
                    _lerpBuffer.Remove(_stateB);
                    _lerpTime = SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTime;
                    _stateB.Preload(this);
                }
            }

            if (_stateB != null)
            {
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesArray[preloadData.EntityId];
                    var fields = ClassDataDict[entity.ClassId].Fields;
                    
                    unsafe
                    {
                        byte* entityPtr = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref entity);
                        fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr =
                                   _stateB.FinalReader.RawData)
                        {
                            for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                            {
                                var interpolatedCache = preloadData.InterpolatedCaches[j];
                                {
                                    interpolatedCache.Interpolator(
                                        initialDataPtr + interpolatedCache.InitialDataOffset,
                                        nextDataPtr + interpolatedCache.StateReaderOffset,
                                        entityPtr + fields[interpolatedCache.Field].Offset,
                                        fTimer);
                                }
                            }
                        }
                    }
                }
                _timer += CurrentDelta * (0.94f + 0.02f * _lerpBuffer.Count);
                if (_timer >= _lerpTime)
                {
                    _statesPool.Enqueue(_stateA);
                    _stateA = _stateB;
                    _stateB = null;
                    //goto state b
                    ReadEntityStates();
                    _timer -= _lerpTime;
                }
            }

            foreach (var inputCommand in _inputCommands)
            {
                _localPeer.Send(inputCommand, DeliveryMethod.Unreliable);
            }
        }

        private void ReadEntityStates()
        {
            ServerTick = _stateA.Tick;
            var reader = _stateA.FinalReader;
            _currentReader = reader;

            if (_stateA.IsBaseline)
            {
                while (reader.AvailableBytes > 0)
                {
                    ushort entityId = reader.GetUShort();
                    ReadEntityState(entityId, true);
                }
 
                if (reader.AvailableBytes != 0)
                {
                    Logger.LogWarning($"[CEM] Something broken, available: {reader.AvailableBytes}");
                }
            }
            else
            {
                for(int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    reader.SetPosition(preloadData.DataOffset);
                    if (!ReadEntityState(preloadData.EntityId, preloadData.EntityFieldsOffset == -1))
                        return;
                    //Logger.Log($"[{entity.Id}] READ: {reader.Position - initialReadSize}");
                }
            }

            _lastProcessedTick = _stateA.ProcessedTick;

            //reset entities
            if (_inputCommands.Count > 0)
            {
                PredictionReset = true;
                foreach (var internalEntity in _ownedEntities)
                {
                    _predictWriter.Reset();
                    _predictedEntities[internalEntity.Id].MakeDiff(0, _predictWriter, true);
                    _predictReader.SetSource(_predictWriter.Data, StateSerializer.HeaderSize, _predictWriter.Length);
                    _currentReader = _predictReader;
                    _fullSyncRead = true;
                    ReadEntity(internalEntity);
                }
                PredictionReset = false;
                
                int commandsToRemove = 0;
                //reapply input
                foreach (var inputCommand in _inputCommands)
                {
                    ushort inputTick = BitConverter.ToUInt16(inputCommand.Data, 4);

                    if (SequenceDiff(_lastProcessedTick, inputTick) >= 0)
                    {
                        //remove processed inputs
                        commandsToRemove++;
                    }
                    else
                    {
                        //reapply input data
                        _inputReader.SetSource(inputCommand.Data, 6, inputCommand.Length);
                        foreach(var entity in GetEntities<HumanControllerLogic>())
                        {
                            entity.ReadInput(_inputReader);
                            entity.ControlledEntity?.Update();
                        }
                        _inputReader.Clear();
                    }
                }
                _inputCommands.RemoveFromStart(commandsToRemove);
            }
        }

        private bool _fullSyncRead;

        private bool ReadEntityState(ushort entityInstanceId, bool fullSync)
        {
            _fullSyncRead = fullSync;
            var entity = EntitiesArray[entityInstanceId];

            //full sync
            if (_fullSyncRead)
            {
                byte version = _currentReader.GetByte();
                ushort classId = _currentReader.GetUShort();

                //remove old entity
                if (entity != null && entity.Version != version)
                {
                    //this can be only on logics (not on singletons)
                    Logger.Log($"[CEM] Replace entity by new: {version}");
                    ((EntityLogic)entity).DestroyInternal();
                }
                //create new
                entity = AddEntity(
                    new EntityParams(
                        classId, 
                        entityInstanceId,
                        version, 
                        this), 
                    ReadEntity);
                Logger.Log($"[CEM] EntityCreated: {entityInstanceId} cid: {entity.ClassId}, v: {version}");
            }
            else
            {
                if (entity == null)
                {
                    Logger.LogError($"EntityNull? : {entityInstanceId}");
                    return false;
                }
                //read old
                ReadEntity(entity);
            }

            return true;
        }
        
        private unsafe void ReadEntity(InternalEntity entity)
        {
            var classData = ClassDataDict[entity.ClassId];
            var fixedFields = classData.Fields;
            byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref entity);
            int readerPosition = _currentReader.Position;

            StateSerializer stateSerializer = null;
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            ref byte[] interpolatePrevData = ref _interpolatePrevData[entity.Id];
            
            if (!PredictionReset && entity.IsLocalControlled)
            {
                stateSerializer = _predictedEntities[entity.Id];
                if (_fullSyncRead)
                {
                    stateSerializer ??= new StateSerializer();
                    stateSerializer.Init(classData, entity);
                    
                    _predictedEntities[entity.Id] = stateSerializer;
                    _ownedEntities.Add(entity);
                    
                    Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
                    Utils.ResizeOrCreate(ref interpolatePrevData, classData.InterpolatedFieldsSize);
                }
            }
            else if (_fullSyncRead)
            {
                Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
            }
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            int fixedDataOffset = 0;

            fixed (byte* rawData = _currentReader.RawData, interpDataPtr = interpolatedInitialData)
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var entityFieldInfo = ref fixedFields[i];
                    if (!_fullSyncRead && (rawData[fieldsFlagsOffset + i / 8] & (1 << (i % 8))) == 0)
                    {
                        fixedDataOffset += entityFieldInfo.IntSize;
                        continue;
                    }
                    byte* fieldPtr = entityPtr + entityFieldInfo.Offset;
                    byte* readDataPtr = rawData + readerPosition;

                    switch (entityFieldInfo.Type)
                    {
                        case FixedFieldType.None:
                            if (i < classData.InterpolatedMethods.Length && (entity.IsServerControlled || (!PredictionReset && _fullSyncRead)) )
                            {
                                //this is interpolated save for future
                                Unsafe.CopyBlock(interpDataPtr + fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                            }
                            Unsafe.CopyBlock(fieldPtr, readDataPtr, entityFieldInfo.Size);
                            stateSerializer?.WritePredicted(fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                            break;

                        case FixedFieldType.EntityId:
                            ushort entityId = *(ushort*)(readDataPtr);
                            Unsafe.AsRef<EntityLogic>(fieldPtr) = entity.EntityManager.GetEntityById(entityId);
                            stateSerializer?.WritePredicted(fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                            break;
                    }
                    readerPosition += entityFieldInfo.IntSize;
                    fixedDataOffset += entityFieldInfo.IntSize;
                }
            }
            _currentReader.SetPosition(readerPosition);
            entity.OnSync();
        }

        private byte[] _compressionBuffer;
        
        public void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            switch (packetType)
            {
                case PacketEntityFullSync:
                {
                    int decompressedSize = reader.GetInt();
                    
                    if (_compressionBuffer == null)
                        _compressionBuffer = new byte[decompressedSize];
                    else if (_compressionBuffer.Length < decompressedSize)
                        Array.Resize(ref _compressionBuffer, decompressedSize);
                    int decodedBytes = LZ4Codec.Decode(
                        reader.RawData,
                        reader.Position,
                        reader.AvailableBytes,
                        _compressionBuffer,
                        0,
                        decompressedSize);
                    if (decodedBytes != decompressedSize)
                    {
                        Logger.LogError("Error on decompress");
                    }
                    
                    _stateA = new ServerStateData
                    {
                        IsBaseline = true
                    };
                    _stateA.FinalReader.SetSource(_compressionBuffer, 0, decompressedSize);
                    _stateA.Tick = _stateA.FinalReader.GetUShort();
                    ReadEntityStates();
                    _isSyncReceived = true;
                    break;
                }
                
                case PacketEntitySyncLast:
                case PacketEntitySync:
                {
                    bool isLastPart = packetType == PacketEntitySyncLast;
                    ushort newServerTick = reader.GetUShort();
                    if (SequenceDiff(newServerTick, ServerTick) <= 0)
                    {
                        reader.Recycle();
                        break;
                    }
                    
                    if(!_receivedStates.TryGetValue(newServerTick, out var serverState))
                    {
                        if (_receivedStates.Count > MaxSavedStateDiff)
                        {
                            var minimal = _receivedStates.Keys[0];
                            if (SequenceDiff(newServerTick, minimal) > 0)
                            {
                                serverState = _receivedStates[minimal];
                                _receivedStates.Remove(minimal);
                                serverState.Reset(newServerTick);
                            }
                            else
                            {
                                reader.Recycle();
                                break;
                            }
                        }
                        else if (_statesPool.Count > 0)
                        {
                            serverState = _statesPool.Dequeue();
                            serverState.Reset(newServerTick);
                        }
                        else
                        {
                            serverState = new ServerStateData { Tick = newServerTick };
                        }
                        _receivedStates.Add(newServerTick, serverState);
                    }
                    
                    //if got full state - add to lerp buffer
                    if(serverState.ReadPart(isLastPart, reader))
                    {
                        _receivedStates.Remove(serverState.Tick);
                        
                        if (_lerpBuffer.Count >= InterpolateBufferSize)
                        {
                            if (SequenceDiff(serverState.Tick, _lerpBuffer.Min.Tick) > 0)
                            {
                                _lerpBuffer.Remove(_lerpBuffer.Min);
                                _lerpBuffer.Add(serverState);
                            }
                            else
                            {
                                _statesPool.Enqueue(serverState);
                            }
                        }
                        else
                        {
                            _lerpBuffer.Add(serverState);
                        }
                    }
                    break;
                }

                case PacketEntityCall:
                {
                    ushort entityInstanceId = reader.GetUShort();
                    byte packetId = reader.GetByte();
                    GetEntityById(entityInstanceId)?.ProcessPacket(packetId, reader);
                    reader.Recycle();
                    break;
                }
            }
        }
    }
}