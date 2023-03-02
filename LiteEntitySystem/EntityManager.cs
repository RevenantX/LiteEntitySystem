using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;
using LiteNetLib;

namespace LiteEntitySystem
{
    public delegate T EntityConstructor<out T>(EntityParams entityParams) where T : InternalEntity;
    
    [Flags]
    public enum ExecuteFlags : byte
    {
        None = 0,
        SendToOwner = 1,
        SendToOther = 1 << 1,
        ExecuteOnPrediction = 1 << 2,
        ExecuteOnServer = 1 << 3,
        ExecuteOnClinet = 1 << 4,
        All = SendToOther | SendToOwner | ExecuteOnPrediction | ExecuteOnServer
    }

    public enum NetworkMode : byte
    {
        Client,
        Server
    }

    public enum UpdateMode
    {
        Normal,
        PredictionRollback
    }
    
    public enum MaxHistorySize : byte
    {
        Size16 = 16,
        Size32 = 32,
        Size64 = 64,
        Size128 = 128
    }

    /// <summary>
    /// Base class for client and server manager
    /// </summary>
    public abstract class EntityManager
    {
        /// <summary>
        /// Maximum synchronized (without LocalOnly) entities
        /// </summary>
        public const int MaxSyncedEntityCount = 8192;

        public const int MaxEntityCount = MaxSyncedEntityCount * 2;
        
        public const byte ServerPlayerId = 0;
        
        /// <summary>
        /// Invalid entity id
        /// </summary>
        public const ushort InvalidEntityId = 0;
        
        /// <summary>
        /// Total entities count (including local)
        /// </summary>
        public ushort EntitiesCount { get; private set; }

        /// <summary>
        /// Current tick
        /// </summary>
        public ushort Tick => _tick;

        /// <summary>
        /// Interpolation time between logic and render
        /// </summary>
        public float LerpFactor => _accumulator / (float)_deltaTimeTicks;
        
        /// <summary>
        /// Current update mode (can be used inside entities to separate logic for rollbacks)
        /// </summary>
        public UpdateMode UpdateMode { get; protected set; }
        
        /// <summary>
        /// Current mode (Server or Client)
        /// </summary>
        public readonly NetworkMode Mode;
        
        /// <summary>
        /// Is server
        /// </summary>
        public readonly bool IsServer;

        /// <summary>
        /// Is client
        /// </summary>
        public readonly bool IsClient;
        
        /// <summary>
        /// FPS of game logic
        /// </summary>
        public readonly int FramesPerSecond;
        
        /// <summary>
        /// Fixed delta time
        /// </summary>
        public readonly double DeltaTime;

        /// <summary>
        /// Fixed delta time (float for less precision)
        /// </summary>
        public readonly float DeltaTimeF;
        
        /// <summary>
        /// Size of history (in ticks) for lag compensation. Tune for your game fps 
        /// </summary>
        public MaxHistorySize MaxHistorySize = MaxHistorySize.Size32;

        /// <summary>
        /// Local player id (0 on server)
        /// </summary>
        public byte PlayerId => InternalPlayerId;
        
        public bool InRollBackState => UpdateMode == UpdateMode.PredictionRollback;
        public bool InNormalState => UpdateMode == UpdateMode.Normal;
        
        internal const byte PacketDiffSync = 1;
        internal const byte PacketClientSync = 2;
        internal const byte PacketBaselineSync = 3;
        internal const byte PacketDiffSyncLast = 4;

        protected const int MaxFieldSize = 1024;
        protected const int MaxSavedStateDiff = 30;
        protected const ushort FirstEntityId = 1;
        internal const int MaxParts = 256;
        private const int MaxTicksPerUpdate = 5;

        public double VisualDeltaTime { get; private set; }
        public const int MaxPlayers = byte.MaxValue-1;

        protected int MaxSyncedEntityId = -1; //current maximum id
        protected int MaxLocalEntityId = -1;
        protected ushort _tick;
        
        protected readonly EntityFilter<InternalEntity> AliveEntities = new();
        protected readonly EntityFilter<EntityLogic> LagCompensatedEntities = new();

        private readonly double _stopwatchFrequency;
        private readonly Stopwatch _stopwatch = new();
        private readonly Queue<ushort> _localIdQueue = new();
        private readonly SingletonEntityLogic[] _singletonEntities;
        private readonly EntityFilter[] _entityFilters;
        private readonly Dictionary<Type, ushort> _registeredTypeIds = new();

        internal readonly InternalEntity[] EntitiesDict = new InternalEntity[MaxEntityCount+1];
        internal readonly EntityClassData[] ClassDataDict;

        private readonly long _deltaTimeTicks;
        private long _accumulator;
        private long _lastTime;
        private ushort _localIdCounter = MaxSyncedEntityCount;
        private bool _lagCompensationEnabled;

        internal byte InternalPlayerId;
        protected readonly InputProcessor InputProcessor;

        public static void RegisterFieldType<T>(InterpolatorDelegateWithReturn<T> interpolationDelegate) where T : unmanaged
        {
            ValueProcessors.RegisteredProcessors[typeof(T)] = new UserTypeProcessor<T>(interpolationDelegate);
        }
        
        public static void RegisterFieldType<T>() where T : unmanaged
        {
            ValueProcessors.RegisteredProcessors[typeof(T)] = new UserTypeProcessor<T>(null);
        }

        private static void RegisterBasicFieldType<T>(ValueTypeProcessor<T> proc) where T : unmanaged
        {
            ValueProcessors.RegisteredProcessors.Add(typeof(T), proc);
        }

        static EntityManager()
        {
#if UNITY_ANDROID
            if (IntPtr.Size == 4)
                K4os.Compression.LZ4.LZ4Codec.Enforce32 = true;
#endif
            RegisterBasicFieldType(new ValueTypeProcessorByte());
            RegisterBasicFieldType(new ValueTypeProcessorSByte());
            RegisterBasicFieldType(new ValueTypeProcessorShort());
            RegisterBasicFieldType(new ValueTypeProcessorUShort());
            RegisterBasicFieldType(new ValueTypeProcessorInt());
            RegisterBasicFieldType(new ValueTypeProcessorUInt());
            RegisterBasicFieldType(new ValueTypeProcessorLong());
            RegisterBasicFieldType(new ValueTypeProcessorULong());
            RegisterBasicFieldType(new ValueTypeProcessorFloat());
            RegisterBasicFieldType(new ValueTypeProcessorDouble());
            RegisterBasicFieldType(new ValueTypeProcessorBool());
            RegisterBasicFieldType(new ValueTypeProcessorEntitySharedReference());
            RegisterFieldType<FloatAngle>(FloatAngle.Lerp);
        }

        protected EntityManager(EntityTypesMap typesMap, InputProcessor inputProcessor, NetworkMode mode, byte framesPerSecond)
        {
            ClassDataDict = new EntityClassData[typesMap.MaxId+1];

            ushort filterCount = 0;
            ushort singletonCount = 0;
            
            //preregister some types
            _registeredTypeIds.Add(typeof(ControllerLogic), filterCount++);
            
            foreach (var kv in typesMap.RegisteredTypes)
            {
                var entType = kv.Key;

                ClassDataDict[kv.Value.ClassId] = new EntityClassData(
                    entType.IsSubclassOf(typeof(SingletonEntityLogic)) ? singletonCount++ : filterCount++, 
                    entType, 
                    kv.Value);
                _registeredTypeIds.Add(entType, ClassDataDict[kv.Value.ClassId].FilterId);
                //Logger.Log($"Register: {entType.Name} ClassId: {classId}");
            }

            foreach (var registeredType in typesMap.RegisteredTypes.Values)
            {
                //map base ids
                ClassDataDict[registeredType.ClassId].PrepareBaseTypes(_registeredTypeIds, ref singletonCount, ref filterCount);
            }

            _entityFilters = new EntityFilter[filterCount];
            _singletonEntities = new SingletonEntityLogic[singletonCount];

            InputProcessor = inputProcessor;
            
            Mode = mode;
            IsServer = Mode == NetworkMode.Server;
            IsClient = Mode == NetworkMode.Client;
            FramesPerSecond = framesPerSecond;
            DeltaTime = 1.0 / framesPerSecond;
            DeltaTimeF = (float) DeltaTime;
            _stopwatchFrequency = 1.0 / Stopwatch.Frequency;
            _deltaTimeTicks = (long)(DeltaTime * Stopwatch.Frequency);
        }

        /// <summary>
        /// Remove all entities and reset all counters and timers
        /// </summary>
        public void Reset()
        {
            EntitiesCount = 0;

            _tick = 0;
            VisualDeltaTime = 0.0;
            _accumulator = 0;
            _lastTime = 0;
            InternalPlayerId = 0;
            _localIdCounter = MaxSyncedEntityCount;
            _localIdQueue.Clear();
            _stopwatch.Restart();
            AliveEntities.Clear();

            for (int i = 0; i < _singletonEntities.Length; i++)
            {
                _singletonEntities[i]?.DestroyInternal();
                _singletonEntities[i] = null;
            }

            for (int i = FirstEntityId; i < EntitiesDict.Length; i++)
            {
                EntitiesDict[i]?.DestroyInternal();
                EntitiesDict[i] = null;
            }

            for (int i = 0; i < _entityFilters.Length; i++)
            {
                _entityFilters[i] = null;
            }
        }

        /// <summary>
        /// Get entity by id
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null if id == InvalidEntityId or entity is another type or version</returns>
        public T GetEntityById<T>(EntitySharedReference id) where T : InternalEntity
        {
            return id.Id != InvalidEntityId
                ? EntitiesDict[id.Id] is T entity && entity.Version == id.Version ? entity : null
                : null;
        }
        
        /// <summary>
        /// Try get entity by id
        /// throws exception if entity is null or invalid type
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <param name="entity">out entity if exists otherwise null</param>
        /// <returns>true if it exists, false if id == InvalidEntityId or entity is another type or version</returns>
        public bool TryGetEntityById<T>(EntitySharedReference id, out T entity) where T : InternalEntity
        {
            entity = id.Id != InvalidEntityId
                ? EntitiesDict[id.Id] is T castedEnt && castedEnt.Version == id.Version ? castedEnt : null
                : null;
            return entity != null;
        }
        
        private EntityFilter<T> GetEntitiesInternal<T>() where T : InternalEntity
        {
            if (!_registeredTypeIds.TryGetValue(typeof(T), out ushort typeId))
                throw new Exception($"Unregistered type: {typeof(T)}");
            
            ref var entityFilter = ref _entityFilters[typeId];
            EntityFilter<T> typedFilter;
            if (entityFilter != null)
            {
                typedFilter = (EntityFilter<T>)entityFilter;
                typedFilter.Refresh();
                return typedFilter;
            }
            
            typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = FirstEntityId; i <= MaxSyncedEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt)
                    typedFilter.Add(castedEnt);
            }
            for (int i = MaxSyncedEntityCount; i <= MaxLocalEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt)
                    typedFilter.Add(castedEnt);
            }
            typedFilter.Refresh();
            return typedFilter;
        }

        /// <summary>
        /// Get all entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetEntities<T>() where T : EntityLogic
        {
            return GetEntitiesInternal<T>();
        }
        
        /// <summary>
        /// Get all controller entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetControllers<T>() where T : ControllerLogic
        {
            return GetEntitiesInternal<T>();
        }

        /// <summary>
        /// Get existing singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity, can throw exceptions on invalid type</returns>
        public T GetSingleton<T>() where T : SingletonEntityLogic
        {
            return (T)_singletonEntities[_registeredTypeIds[typeof(T)]];
        }
        
        /// <summary>
        /// Get existing singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <param name="singleton">Singleton entity, can throw exceptions on invalid type</param>
        public void GetSingleton<T>(out T singleton) where T : SingletonEntityLogic
        {
            singleton = (T)_singletonEntities[_registeredTypeIds[typeof(T)]];
        }

        /// <summary>
        /// Get singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity or null if it didn't exists</returns>
        public T GetSingletonSafe<T>() where T : SingletonEntityLogic
        {
            return _registeredTypeIds.TryGetValue(typeof(T), out ushort registeredId) ? _singletonEntities[registeredId] as T : null;
        }

        /// <summary>
        /// Is singleton exists and has correct type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public bool HasSingleton<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[_registeredTypeIds[typeof(T)]] is T;
        }

        /// <summary>
        /// Try get singleton entity
        /// </summary>
        /// <param name="singleton">result singleton entity</param>
        /// <typeparam name="T">Singleton type</typeparam>
        /// <returns>true if entity exists</returns>
        public bool TryGetSingleton<T>(out T singleton) where T : SingletonEntityLogic
        {
            var s = _singletonEntities[_registeredTypeIds[typeof(T)]];
            if (s != null)
            {
                singleton = (T)s;
                return true;
            }
            singleton = null;
            return false;
        }
        
        /// <summary>
        /// Add local entity that will be not synchronized
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null if entities limit is reached (65535 - <see cref="MaxSyncedEntityCount"/>)</returns>
        public T AddLocalEntity<T>(Action<T> initMethod = null) where T : InternalEntity
        {
            if (_localIdCounter == 0)
            {
                Logger.LogError("Max local entities count reached");
                return null;
            }
            
            var entityParams = new EntityParams(
                EntityClassInfo<T>.ClassId,
                _localIdQueue.Count > 0 ? _localIdQueue.Dequeue() : _localIdCounter++, 
                0,
                this);
            var entity = (T)AddEntity(entityParams);
            if (IsClient && entity is EntityLogic logic)
            {
                logic.InternalOwnerId = InternalPlayerId;
            }

            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            return entity;
        }

        protected InternalEntity AddEntity(EntityParams entityParams)
        {
            if (entityParams.Id == InvalidEntityId || entityParams.Id >= EntitiesDict.Length)
            {
                throw new ArgumentException($"Invalid entity id: {entityParams.Id}");
            }

            if (entityParams.ClassId >= ClassDataDict.Length)
            {
                throw new Exception($"Unregistered entity class: {entityParams.ClassId}");
            }
            
            var classData = ClassDataDict[entityParams.ClassId];
            if (classData.EntityConstructor == null)
            {
                throw new Exception($"Unregistered entity class: {entityParams.ClassId}");
            }
            var entity = classData.EntityConstructor(entityParams);
            entity.RegisterRpcInternal();

            if(entityParams.Id < MaxSyncedEntityCount)
                MaxSyncedEntityId = MaxSyncedEntityId < entityParams.Id ? entityParams.Id : MaxSyncedEntityId;
            else
                MaxLocalEntityId = MaxLocalEntityId < entityParams.Id ? entityParams.Id : MaxLocalEntityId;
            
            EntitiesDict[entity.Id] = entity;
            EntitiesCount++;
            return entity;
        }

        protected void ConstructEntity(InternalEntity e)
        {
            ref var classData = ref ClassDataDict[e.ClassId];
            
            e.CallConstruct();

            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = (SingletonEntityLogic)e;
                foreach (int baseId in classData.BaseIds)
                    _singletonEntities[baseId] = (SingletonEntityLogic)e;
            }
            else
            {
                _entityFilters[classData.FilterId]?.Add(e);
                foreach (int baseId in classData.BaseIds)
                    _entityFilters[baseId]?.Add(e);
            }
            
            if (IsEntityAlive(classData, e))
            {
                AliveEntities.Add(e);
                if(!e.IsLocal && e is EntityLogic entityLogic && entityLogic.HasLagCompensation)
                    LagCompensatedEntities.Add(entityLogic);
            }
        }

        private bool IsEntityAlive(EntityClassData classData, InternalEntity entity)
        {
            return classData.IsUpdateable && (IsServer || entity.IsLocal || (IsClient && (classData.UpdateOnClient || classData.HasRemotePredictedFields)));
        }

        internal void RemoveEntity(InternalEntity e)
        {
            ref var classData = ref ClassDataDict[e.ClassId];
            
            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = null;
                foreach (int baseId in classData.BaseIds) 
                    _singletonEntities[baseId] = null;
            }
            else
            {
                _entityFilters[classData.FilterId]?.Remove(e);
                foreach (int baseId in classData.BaseIds) 
                    _entityFilters[baseId]?.Remove(e);
            }

            if (IsEntityAlive(classData, e))
            {
                AliveEntities.Remove(e);
                if(!e.IsLocal && e is EntityLogic entityLogic && entityLogic.HasLagCompensation)
                    LagCompensatedEntities.Remove(entityLogic);
            }
            if (classData.IsLocalOnly)
                _localIdQueue.Enqueue(e.Id);
            EntitiesDict[e.Id] = null;
            EntitiesCount--;
            //Logger.Log($"{Mode} - RemoveEntity: {e.Id}");
        }

        internal abstract NetPlayer GetPlayer(byte playerId);

        public void EnableLagCompensation(byte playerId)
        {
            if (_lagCompensationEnabled || playerId == ServerPlayerId)
                return;

            var player = GetPlayer(playerId);
            if (player == null)
                return;

            _lagCompensationEnabled = true;
            //Logger.Log($"C: {IsClient} compensated: {player.SimulatedServerTick} =====");
            foreach (var entity in LagCompensatedEntities)
            {
                entity.EnableLagCompensation(player);
                //entity.DebugPrint();
            }
        }

        public void DisableLagCompensation()
        {
            if(!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            //Logger.Log($"restored: {Tick} =====");
            foreach (var entity in LagCompensatedEntities)
            {
                entity.DisableLagCompensation();
                //entity.DebugPrint();
            }
        }

        protected abstract void OnLogicTick();

        /// <summary>
        /// Main update method, updates internal fixed timer and do all other stuff
        /// </summary>
        public virtual void Update()
        {
            if(!_stopwatch.IsRunning)
                _stopwatch.Start();

            long elapsedTicks = _stopwatch.ElapsedTicks;
            long ticksDelta = elapsedTicks - _lastTime;
            VisualDeltaTime = ticksDelta * _stopwatchFrequency;
            _accumulator += ticksDelta;
            _lastTime = elapsedTicks;

            int updates = 0;
            while (_accumulator >= _deltaTimeTicks)
            {
                //Lag
                if (updates >= MaxTicksPerUpdate)
                {
                    _accumulator = 0;
                    return;
                }
                _tick++;
                OnLogicTick();

                _accumulator -= _deltaTimeTicks;
                updates++;
            }
        }
    }
}
