using System;
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
    
    public abstract partial class EntityManager
    {
        public const int MaxEntityCount = 8192;
        public const int MaxEntityIndex = MaxEntityCount-1;
        public const ushort InvalidEntityId = MaxEntityCount;

        protected const byte PacketEntitySync = 1;
        protected const byte PacketEntityCall = 2;
        protected const byte PacketClientSync = 3;
        protected const byte PacketEntityFullSync = 4;
        protected const byte PacketEntitySyncLast = 5;
        
        public const int MaxSavedStateDiff = 32;
        
        private const int MaxSequence = 65536;
        private const int MaxSeq2 = MaxSequence / 2;
        private const int MaxSeq15 = MaxSequence + MaxSeq2;

        protected const byte MaxParts = 255;

        protected double _accumulator;
        private readonly double _stopwatchFrequency;
        private long _lastTime;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        protected double CurrentDelta { get; private set; }
        
        protected readonly InternalEntity[] EntitiesArray = new InternalEntity[MaxEntityCount];
        protected readonly EntityFilter<InternalEntity> AliveEntities = new EntityFilter<InternalEntity>();
        public int EntitiesCount { get; private set; }
        protected int MaxEntityId = -1; //current maximum id

        public ushort Tick { get; private set; }
        public ushort ServerTick { get; protected set; }
        
        public readonly NetworkMode Mode;
        public readonly bool IsServer;
        public readonly bool IsClient;

        public readonly int FramesPerSecond;
        public readonly float DeltaTime;
        public readonly NetSerializer Serializer = new NetSerializer();
        public virtual byte PlayerId => 0;

        private bool _isStarted;

        protected bool IsStarted => _isStarted;

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
            RegisterInterpolator((float prev, float current, out float result, float t) =>
            {
                result = prev + (current - prev) * t;
            });
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

        protected InternalEntity AddEntity(EntityParams entityParams, Action<InternalEntity> onConstruct)
        {
            if (entityParams.Id >= InvalidEntityId)
            {
                throw new ArgumentException($"Invalid entity id: {entityParams.Id}");
            } 
            MaxEntityId = MaxEntityId < entityParams.Id ? entityParams.Id : MaxEntityId;
            
            var classData = ClassDataDict[entityParams.ClassId];
            var entity = classData.EntityConstructor(entityParams);
            onConstruct?.Invoke(entity);
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
            
            EntitiesArray[entity.Id] = entity;
            EntitiesCount++;
            
            return entity;
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
            if (!_isStarted)
            {
                SetupEntityInfo();
                _stopwatch.Start();
                _isStarted = true;
            }
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