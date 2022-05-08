using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    using InternalEntity = EntityManager.InternalEntity;
    
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncVar : Attribute
    {
        internal readonly bool IsInterpolated;
        internal readonly string MethodName;

        public SyncVar()
        {
            
        }
        
        public SyncVar(bool isInterpolated)
        {
            IsInterpolated = isInterpolated;
        }
        
        public SyncVar(string methodName)
        {
            MethodName = methodName;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class RollbackVar : Attribute { }
    
    [AttributeUsage(AttributeTargets.Class)]
    public class UpdateableEntity : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public class ServerOnly : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public class RemoteCall : Attribute
    {
        public readonly ExecuteFlags Flags;
        
        internal byte Id = byte.MaxValue;
        internal int DataSize;

        public RemoteCall(ExecuteFlags flags)
        {
            Flags = flags;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class SyncableRemoteCall : Attribute
    {
        internal byte Id = byte.MaxValue;
        internal int DataSize;
        internal MethodCallDelegate MethodDelegate;
    }

    public readonly struct EntityParams
    {
        public readonly ushort ClassId;
        public readonly ushort Id;
        public readonly byte Version;
        public readonly EntityManager EntityManager;

        internal EntityParams(
            ushort classId,
            ushort id,
            byte version,
            EntityManager entityManager)
        {
            ClassId = classId;
            Id = id;
            Version = version;
            EntityManager = entityManager;
        }
    }

    public abstract partial class EntityManager
    {
        public abstract class InternalEntity : IComparable<InternalEntity>
        {
            public readonly ushort ClassId;
            public readonly ushort Id;
            public readonly EntityManager EntityManager;
            public readonly byte Version;
            public abstract bool IsLocalControlled { get; }
            public bool IsServerControlled => !IsLocalControlled;

            internal abstract bool IsControlledBy(byte playerId);

            public virtual void DebugPrint()
            {
                
            }

            public virtual void Update()
            {
            }

            public virtual void OnConstructed()
            {
            }

            protected InternalEntity(EntityParams entityParams)
            {
                EntityManager = entityParams.EntityManager;
                Id = entityParams.Id;
                ClassId = entityParams.ClassId;
                Version = entityParams.Version;
            }

            protected void ExecuteRemoteCall<T>(Action<T> methodToCall, T value) where T : struct
            {
                if (methodToCall.Target != this)
                    throw new Exception("You can call this only on this class methods");
                var classData = EntityManager.ClassDataDict[ClassId];
                if(!classData.RemoteCalls.TryGetValue(methodToCall.Method, out var remoteCallInfo))
                    throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");
                if (EntityManager.IsServer)
                {
                    if ((remoteCallInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                        methodToCall(value);
                    ((ServerEntityManager)EntityManager).EntitySerializers[Id].AddRemoteCall(value, remoteCallInfo);
                }
                else if(IsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                {
                    methodToCall(value);
                }
            }
            
            protected void ExecuteRemoteCall<T>(Action<T[]> methodToCall, T[] value, int count) where T : struct
            {
                if (methodToCall.Target != this)
                    throw new Exception("You can call this only on this class methods");
                var classData = EntityManager.ClassDataDict[ClassId];
                if(!classData.RemoteCalls.TryGetValue(methodToCall.Method, out var remoteCallInfo))
                    throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");
                if (EntityManager.IsServer)
                {
                    if ((remoteCallInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                        methodToCall(value);
                    ((ServerEntityManager)EntityManager).EntitySerializers[Id].AddRemoteCall(value, count, remoteCallInfo);
                }
                else if(IsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                {
                    methodToCall(value);
                }
            }

            public int CompareTo(InternalEntity other)
            {
                return Id - other.Id;
            }
        }
    }

    public abstract class EntityLogic : InternalEntity
    {
        [SyncVar(nameof(OnParentChange))] 
        private ushort _parentId = EntityManager.InvalidEntityId;
        
        [SyncVar(nameof(OnDestroyChange))] 
        private bool _isDestroyed;
        
        [SyncVar(nameof(OnOwnerChange))]
        internal ushort InternalOwnerId;

        public bool IsDestroyed => _isDestroyed;
        public readonly List<EntityLogic> Childs = new List<EntityLogic>();
        public ushort OwnerId => InternalOwnerId;
        public override bool IsLocalControlled => InternalOwnerId == EntityManager.PlayerId;

        internal override bool IsControlledBy(byte playerId)
        {
            return playerId == InternalOwnerId;
        }

        internal void DestroyInternal()
        {
            _isDestroyed = true;
            OnDestroy();
            EntityManager.RemoveEntity(this);
            EntityManager.GetEntityById(_parentId)?.Childs.Remove(this);
            foreach (var e in Childs)
                e.DestroyInternal();
        }

        public void Destroy()
        {
            if (EntityManager.IsClient || _isDestroyed)
                return;
            DestroyInternal();
        }

        private void OnOwnerChange(ushort prevOwner)
        {
            var ownedEntities = ((ClientEntityManager)EntityManager).OwnedEntities;
            if(IsLocalControlled)
                ownedEntities.Add(this);
            else if(prevOwner == EntityManager.PlayerId)
                ownedEntities.Remove(this);
        }

        private void OnDestroyChange(bool prevValue)
        {
            if (_isDestroyed)
            {
                DestroyInternal();
            }
        }

        public void SetParent(EntityLogic parentEntity)
        {
            if (EntityManager.IsClient)
                return;
            
            ushort id = parentEntity?.Id ?? EntityManager.InvalidEntityId;
            if (id == _parentId)
                return;
            
            ushort oldId = _parentId;
            _parentId = id;
            OnParentChange(oldId);
            
            var newParent = EntityManager.GetEntityById(_parentId);
            InternalOwnerId = newParent?.InternalOwnerId ?? 0;
            if (InternalOwnerId != oldId)
            {
                SetOwner(this, InternalOwnerId);
            }
        }

        private void OnParentChange(ushort oldId)
        {
            EntityManager.GetEntityById(oldId)?.Childs.Remove(this);
            var newParent = EntityManager.GetEntityById(_parentId);
            newParent?.Childs.Add(this);
        }

        private static void SetOwner(EntityLogic entity, ushort ownerId)
        {
            foreach (var child in entity.Childs)
            {
                child.InternalOwnerId = ownerId;
                SetOwner(child, ownerId);
            }
        }

        public T GetParent<T>() where T : EntityLogic
        {
            return _parentId == EntityManager.InvalidEntityId ? null : (T)EntityManager.GetEntityById(_parentId);
        }
        
        protected virtual void OnDestroy()
        {

        }
        
        protected EntityLogic(EntityParams entityParams) : base(entityParams) { }
    }

    public abstract class SingletonEntityLogic : InternalEntity
    {
        public override bool IsLocalControlled => false;
        
        internal override bool IsControlledBy(byte playerId)
        {
            return false;
        }

        protected SingletonEntityLogic(EntityParams entityParams) : base(entityParams) { }
    }

    [UpdateableEntity]
    public abstract class PawnLogic : EntityLogic
    {
        [SyncVar] 
        private ControllerLogic _controller;

        public ControllerLogic Controller
        {
            get => _controller;
            internal set
            {
                InternalOwnerId = value?.InternalOwnerId ?? (GetParent<EntityLogic>()?.InternalOwnerId ?? 0);
                _controller = value;
            }
        }

        public override void Update()
        {
            _controller?.BeforeControlledUpdate();
        }

        protected override void OnDestroy()
        {
            _controller?.OnControlledDestroy();
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams) { }
    }
    
    public abstract class ControllerLogic : InternalEntity
    {
        [SyncVar] 
        internal byte InternalOwnerId;
        
        [SyncVar] 
        private PawnLogic _controlledEntity;

        public byte OwnerId => InternalOwnerId;
        public PawnLogic ControlledEntity => _controlledEntity;
        public override bool IsLocalControlled => InternalOwnerId == EntityManager.PlayerId;

        internal override bool IsControlledBy(byte playerId)
        {
            return InternalOwnerId == playerId;
        }
        
        public virtual void BeforeControlledUpdate()
        {
            
        }

        public void StartControl<T>(T target) where T : PawnLogic
        {
            StopControl();
            _controlledEntity = target;
            _controlledEntity.Controller = this;
        }

        internal void OnControlledDestroy()
        {
            StopControl();
        }

        public void StopControl()
        {
            if (_controlledEntity == null)
                return;
            _controlledEntity.Controller = null;
            _controlledEntity = null;
        }
        
        protected ControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    [ServerOnly]
    public abstract class AiControllerLogic : ControllerLogic
    {
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    [ServerOnly]
    public abstract class AiControllerLogic<T> : AiControllerLogic where T : PawnLogic
    {
        public new T ControlledEntity => (T) base.ControlledEntity;
        
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    public abstract class HumanControllerLogic : ControllerLogic
    {
        public virtual void ReadInput(NetDataReader reader) { }

        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    public abstract class HumanControllerLogic<T> : HumanControllerLogic where T : PawnLogic
    {
        public new T ControlledEntity => (T) base.ControlledEntity;
        
        protected HumanControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }
}