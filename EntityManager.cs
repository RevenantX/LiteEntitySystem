using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LiteNetLib.Utils;

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
    
    public abstract partial class EntityManager
    {
        public const int MaxEntityCount = 8192;
        public const int MaxEntityIndex = MaxEntityCount-1;
        public const ushort InvalidEntityId = MaxEntityCount;
        public const int MaxSavedStateDiff = 6;

        protected const byte PacketEntitySync = 1;
        protected const byte PacketEntityCall = 2;
        protected const byte PacketClientSync = 3;
        protected const byte PacketEntityFullSync = 4;
        protected const byte PacketEntitySyncLast = 5;
        protected const int MaxFieldSize = 1024;
        protected const byte MaxParts = 255;
        
        private const int MaxSequence = 65536;
        private const int MaxSeq2 = MaxSequence / 2;
        private const int MaxSeq15 = MaxSequence + MaxSeq2;
        
        public int EntitiesCount { get; private set; }
        public ushort Tick { get; private set; }
        public ushort ServerTick { get; protected set; }
        public float LerpFactor => (float)(_accumulator / DeltaTime);
        public UpdateMode UpdateMode { get; protected set; }
        
        public readonly NetworkMode Mode;
        public readonly bool IsServer;
        public readonly bool IsClient;
        public readonly int FramesPerSecond;
        public readonly float DeltaTime;
        public virtual byte PlayerId => 0;
        
        protected double CurrentDelta { get; private set; }
        protected int MaxEntityId = -1; //current maximum id
        protected readonly InternalEntity[] EntitiesArray = new InternalEntity[MaxEntityCount];
        protected readonly EntityFilter<InternalEntity> AliveEntities = new EntityFilter<InternalEntity>();

        private double _accumulator;
        private readonly double _stopwatchFrequency;
        private long _lastTime;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private bool _isStarted;
        private SingletonEntityLogic[] _singletonEntities;
        private EntityFilter[] _entityFilters;
        private ushort _filterRegisteredCount;
        private ushort _singletonRegisteredCount;
        private readonly Dictionary<Type, int> _registeredTypeIds = new Dictionary<Type, int>();
        private int _entityEnumSize = -1;

        internal readonly EntityClassData[] ClassDataDict = new EntityClassData[ushort.MaxValue];

        public void RegisterEntity<TEntity, TEnum>(TEnum id, Func<EntityParams, TEntity> constructor)
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
            
            Logger.Log($"Register entity. Id: {id.ToString()} ({entType}), baseTypes: {classData.BaseTypes.Length}, FilterId: {classData.FilterId}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int SequenceDiff(int newer, int older)
        {
            return (newer - older + MaxSeq15) % MaxSequence - MaxSeq2;
        }

        protected EntityManager(NetworkMode mode, int framesPerSecond)
        {
            Mode = mode;
            IsServer = Mode == NetworkMode.Server;
            IsClient = Mode == NetworkMode.Client;
            FramesPerSecond = framesPerSecond;
            DeltaTime = 1.0f / framesPerSecond;
            _stopwatchFrequency = Stopwatch.Frequency;
            Interpolation.Register<float>((a, b, t) => a + (b - a) * t);
            Interpolation.Register<FloatAngle>(FloatAngle.Lerp);
        }

        public EntityLogic GetEntityById(ushort id)
        {
            return id == InvalidEntityId ? null : (EntityLogic)EntitiesArray[id];
        }
        
        public EntityFilter<T> GetEntities<T>() where T : EntityLogic
        {
            CheckStart();
            
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;

            //initialize new
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i < MaxEntityIndex; i++)
            {
                if(EntitiesArray[i] is T castedEnt && !castedEnt.IsDestroyed)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }
        
        public EntityFilter<T> GetControllers<T>() where T : ControllerLogic
        {
            CheckStart();
            
            ref var entityFilter = ref _entityFilters[_registeredTypeIds[typeof(T)]];
            if (entityFilter != null)
                return (EntityFilter<T>)entityFilter;
            
            var typedFilter = new EntityFilter<T>();
            entityFilter = typedFilter;
            for (int i = 0; i < MaxEntityIndex; i++)
            {
                if(EntitiesArray[i] is T castedEnt)
                    typedFilter.Add(castedEnt);
            }

            return typedFilter;
        }

        public T GetSingleton<T>() where T : SingletonEntityLogic
        {
            return (T)_singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId];
        }

        public T GetSingletonSafe<T>() where T : SingletonEntityLogic
        {
            return _singletonEntities[ClassDataDict[EntityClassInfo<T>.ClassId].FilterId] as T;
        }

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

        protected InternalEntity AddEntity(EntityParams entityParams)
        {
            if (entityParams.Id >= InvalidEntityId)
            {
                throw new ArgumentException($"Invalid entity id: {entityParams.Id}");
            } 
            MaxEntityId = MaxEntityId < entityParams.Id ? entityParams.Id : MaxEntityId;
            
            var classData = ClassDataDict[entityParams.ClassId];
            var entity = classData.EntityConstructor(entityParams);

            EntitiesArray[entity.Id] = entity;
            EntitiesCount++;
            
            return entity;
        }

        protected void ConstructEntity(InternalEntity entity)
        {
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
            entity.OnConstructed();
        }

        internal void RemoveEntity(EntityLogic e)
        {
            var classData = ClassDataDict[e.ClassId];
            _entityFilters[classData.FilterId]?.Remove(e);
            foreach (int baseId in classData.BaseIds)
                _entityFilters[baseId]?.Remove(e);
            if (classData.IsUpdateable)
                AliveEntities.Remove(e);

            EntitiesCount--;
            Logger.Log($"{Mode} - RemoveEntity: {e.Id}");
        }

        protected abstract void OnLogicTick();

        protected void CheckStart()
        {
            if (_isStarted)
                return;

            for (int e = 0; e < _entityEnumSize; e++)
            {
                //map base ids
                var classData = ClassDataDict[e];
                if(classData == null)
                    continue;

                var baseTypes = classData.BaseTypes;
                var baseIds = classData.BaseIds;
                
                for (int i = 0; i < baseIds.Length; i++)
                {
                    if (!_registeredTypeIds.TryGetValue(baseTypes[i], out baseIds[i]))
                    {
                        baseIds[i] = classData.IsSingleton
                            ? _singletonRegisteredCount++
                            : _filterRegisteredCount++;
                        _registeredTypeIds.Add(baseTypes[i], baseIds[i]);
                    }
                    Logger.Log($"Base type of {classData.ClassId} - {baseTypes[i]}");
                }
            }

            _entityFilters = new EntityFilter[_filterRegisteredCount];
            _singletonEntities = new SingletonEntityLogic[_singletonRegisteredCount];
            _stopwatch.Start();
            _isStarted = true;
        }

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
                if (updates > 5)
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