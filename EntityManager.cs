using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [Flags]
    public enum ExecuteFlags : byte
    {
        SendToOwner = 1,
        SendToOther = 1 << 1,
        ExecuteOnPrediction = 1 << 2,
        ExecuteOnServer = 1 << 3,
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
    
    public abstract class EntityManager
    {
        /// <summary>
        /// Maximum synchronized (without LocalOnly) entites
        /// </summary>
        public const int MaxEntityCount = 8192;
        
        /// <summary>
        /// Invalid entity id
        /// </summary>
        public const ushort InvalidEntityId = ushort.MaxValue;
        
        protected const byte PacketDiffSync = 1;
        protected const byte PacketEntityCall = 2;
        protected const byte PacketClientSync = 3;
        protected const byte PacketBaselineSync = 4;
        protected const byte PacketDiffSyncLast = 5;
        protected const int MaxFieldSize = 1024;
        protected const int MaxParts = 256;
        protected const int MaxSavedStateDiff = 6;
        
        private const int MaxTicksPerUpdate = 5;
        
        protected readonly InternalEntity[] EntitiesDict = new InternalEntity[MaxEntityCount];
        protected readonly EntityFilter<InternalEntity> AliveEntities = new EntityFilter<InternalEntity>();
        protected readonly EntityFilter<InternalEntity> LocalEntities = new EntityFilter<InternalEntity>();
        
        /// <summary>
        /// Total entities count (including local)
        /// </summary>
        public int EntitiesCount { get; private set; }
        
        /// <summary>
        /// Current tick
        /// </summary>
        public ushort Tick { get; private set; }

        /// <summary>
        /// Interpolation time between logic and render
        /// </summary>
        public float LerpFactor => (float)(_accumulator / DeltaTime);
        
        /// <summary>
        /// Current update mode (can be used inside entites to separate logic for rollbacks)
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
        public readonly float DeltaTime;
        
        /// <summary>
        /// Local player id (0 on server)
        /// </summary>
        public byte PlayerId => InternalPlayerId;

        protected double CurrentDelta { get; private set; }
        protected int MaxEntityId = -1; //current maximum id

        private readonly double _stopwatchFrequency;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly Dictionary<Type, int> _registeredTypeIds = new Dictionary<Type, int>();
        private readonly Queue<ushort> _localIdQueue = new Queue<ushort>();
        
        private double _accumulator;
        private long _lastTime;
        private bool _isStarted;
        private SingletonEntityLogic[] _singletonEntities;
        private EntityFilter[] _entityFilters;
        private ushort _filterRegisteredCount;
        private ushort _singletonRegisteredCount;
        private int _entityEnumSize = -1;
        private ushort _localIdCounter = MaxEntityCount;

        internal readonly EntityClassData[] ClassDataDict = new EntityClassData[ushort.MaxValue];
        internal byte InternalPlayerId;

        /// <summary>
        /// Register new entity type that will be used in game
        /// </summary>
        /// <param name="id">Enum value that will describe entity class id</param>
        /// <param name="constructor">Constructor of entity</param>
        /// <typeparam name="TEntity">Type of entity</typeparam>
        /// <typeparam name="TEnum">Enum used as classId</typeparam>
        public void RegisterEntityType<TEntity, TEnum>(TEnum id, EntityConstructor<TEntity> constructor)
            where TEntity : InternalEntity where TEnum : Enum
        {
            if (_entityEnumSize == -1)
                _entityEnumSize = Enum.GetValues(typeof(TEnum)).Length;
            
            var entType = typeof(TEntity);
            ushort classId = (ushort)(object)id;
            ref var classData = ref ClassDataDict[classId];
            bool isSingleton = entType.IsSubclassOf(typeof(SingletonEntityLogic));
            classData = new EntityClassData(isSingleton ? _singletonRegisteredCount++ : _filterRegisteredCount++, entType, classId, constructor);
            EntityClassInfo<TEntity>.ClassId = classId;
            _registeredTypeIds.Add(entType, classData.FilterId);
            Logger.Log($"Register: {entType.Name} ClassId: {id.ToString()}({classId})");
        }

        protected EntityManager(NetworkMode mode, byte framesPerSecond)
        {
            Mode = mode;
            IsServer = Mode == NetworkMode.Server;
            IsClient = Mode == NetworkMode.Client;
            FramesPerSecond = framesPerSecond;
            DeltaTime = 1.0f / framesPerSecond;
            _stopwatchFrequency = Stopwatch.Frequency;
            Interpolation.Register<float>(Utils.Lerp);
            Interpolation.Register<FloatAngle>(FloatAngle.Lerp);
        }

        /// <summary>
        /// Get entity by id
        /// throws exception if entity is null or invalid type
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null if id == InvalidEntityId</returns>
        public T GetEntityById<T>(ushort id) where T : InternalEntity
        {
            return id == InvalidEntityId ? null : (T)EntitiesDict[id];
        }
        
        /// <summary>
        /// Get entity by id
        /// </summary>
        /// <param name="id">Id of entity</param>
        /// <returns>Entity if it exists, null otherwise</returns>
        public T GetEntityByIdSafe<T>(ushort id) where T : InternalEntity
        {
            return id == InvalidEntityId ? null : EntitiesDict[id] as T;
        }
        
        /// <summary>
        /// Get all entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetEntities<T>() where T : EntityLogic
        {
            CheckStart();
            
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;

            //initialize new
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i <= MaxEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt && !castedEnt.IsDestroyed)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }
        
        /// <summary>
        /// Get all controller entities with type
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Entity filter that can be used in foreach</returns>
        public EntityFilter<T> GetControllers<T>() where T : ControllerLogic
        {
            CheckStart();
            
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;
            
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i <= MaxEntityId; i++)
            {
                if(EntitiesDict[i] is T castedEnt)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }

        /// <summary>
        /// Get existing singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity, can throw exceptions on invalid type</returns>
        public T GetSingleton<T>() where T : SingletonEntityLogic
        {
            return (T)_singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId];
        }

        /// <summary>
        /// Get singleton entity
        /// </summary>
        /// <typeparam name="T">Singleton entity type</typeparam>
        /// <returns>Singleton entity or null if it didn't exists</returns>
        public T GetSingletonSafe<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId] as T;
        }

        /// <summary>
        /// Is singleton exists and has correct type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public bool HasSingleton<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId] is T;
        }

        /// <summary>
        /// Try get singleton entity
        /// </summary>
        /// <param name="singleton">result singleton entity</param>
        /// <typeparam name="T">Singleton type</typeparam>
        /// <returns>true if entity exists</returns>
        public bool TryGetSingleton<T>(out T singleton) where T : SingletonEntityLogic
        {
            var s = _singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId];
            if (s != null)
            {
                singleton = (T)s;
                return true;
            }
            singleton = null;
            return false;
        }

        protected InternalEntity AddLocalEntity(ushort classId)
        {
            var classData = ClassDataDict[classId];
            var entityParams = new EntityParams(classId, _localIdQueue.Count > 0 ? _localIdQueue.Dequeue() : _localIdCounter++, 0, this);
            var entity = classData.EntityConstructor(entityParams);
            EntitiesCount++;
            return entity;
        }

        protected InternalEntity AddEntity(EntityParams entityParams)
        {
            if (entityParams.Id >= InvalidEntityId)
            {
                throw new ArgumentException($"Invalid entity id: {entityParams.Id}");
            }
            var classData = ClassDataDict[entityParams.ClassId];
            var entity = classData.EntityConstructor(entityParams);
            MaxEntityId = MaxEntityId < entityParams.Id ? entityParams.Id : MaxEntityId;
            EntitiesDict[entity.Id] = entity;
            EntitiesCount++;
            return entity;
        }

        protected void ConstructEntity(InternalEntity entity)
        {
            entity.OnConstructed();
            var classData = ClassDataDict[entity.ClassId];
            if (classData.IsSingleton)
            {
                _singletonEntities[classData.FilterId] = (SingletonEntityLogic)entity;
                foreach (int baseId in classData.BaseIds)
                    _singletonEntities[baseId] = (SingletonEntityLogic)entity;
            }
            else
            {
                _entityFilters[classData.FilterId]?.Add(entity);
                foreach (int baseId in classData.BaseIds)
                    _entityFilters[baseId]?.Add(entity);
            }
            if (classData.IsUpdateable)
                AliveEntities.Add(entity);
            if (classData.IsLocalOnly)
                LocalEntities.Add(entity);
        }

        internal void RemoveEntity(EntityLogic e)
        {
            var classData = ClassDataDict[e.ClassId];
            _entityFilters[classData.FilterId]?.Remove(e);
            foreach (int baseId in classData.BaseIds)
                _entityFilters[baseId]?.Remove(e);
            if (classData.IsUpdateable)
                AliveEntities.Remove(e);
            if (classData.IsLocalOnly)
            {
                _localIdQueue.Enqueue(e.Id);
                LocalEntities.Remove(e);
            }
            EntitiesCount--;
            //Logger.Log($"{Mode} - RemoveEntity: {e.Id}");
        }

        protected abstract void OnLogicTick();

        protected void CheckStart()
        {
            if (_isStarted)
                return;

            for (int e = 0; e < _entityEnumSize; e++)
            {
                //map base ids
                ClassDataDict[e]?.PrepareBaseTypes(_registeredTypeIds, ref _singletonRegisteredCount, ref _filterRegisteredCount);
            }

            _entityFilters = new EntityFilter[_filterRegisteredCount];
            _singletonEntities = new SingletonEntityLogic[_singletonRegisteredCount];
            _stopwatch.Start();
            _isStarted = true;
        }

        /// <summary>
        /// Main update method, updates internal fixed timer and do all other stuff
        /// </summary>
        public virtual void Update()
        {
            long elapsedTicks = _stopwatch.ElapsedTicks;
            CurrentDelta = (elapsedTicks - _lastTime) / _stopwatchFrequency;
            _accumulator += CurrentDelta;
            _lastTime = elapsedTicks;

            int updates = 0;
            while (_accumulator >= DeltaTime)
            {
                //Lag
                if (updates >= MaxTicksPerUpdate)
                {
                    _accumulator = 0;
                    return;
                }
                Tick++;
                OnLogicTick();
                _accumulator -= DeltaTime;
                updates++;
            }
        }
    }
}