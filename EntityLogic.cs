using System;
using System.Collections.Generic;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
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
        internal bool IsArray;

        public RemoteCall(ExecuteFlags flags)
        {
            Flags = flags;
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
        public override bool IsLocalControlled => InternalOwnerId == EntityManager.InternalPlayerId;

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
            if (EntityManager.IsClient && IsLocalControlled)
                ClientManager.OwnedEntities.Remove(this);
            else if (EntityManager.IsServer)
                ServerManager.DestroySavedData(this);
        }

        public void Destroy()
        {
            if (EntityManager.IsClient || _isDestroyed)
                return;
            DestroyInternal();
        }

        private void OnOwnerChange(ushort prevOwner)
        {
            var ownedEntities = ClientManager.OwnedEntities;
            if(IsLocalControlled)
                ownedEntities.Add(this);
            else if(prevOwner == EntityManager.InternalPlayerId)
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

        public virtual void OnLagCompensation(bool enabled)
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
        
        protected void EnableLagCompensation()
        {
            if (EntityManager.IsServer)
                ((ServerEntityManager)EntityManager).EnableLagCompensation(this);
        }

        protected void DisableLagCompensation()
        {
            if (EntityManager.IsServer)
                ((ServerEntityManager)EntityManager).DisableLagCompensation();
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
        public override bool IsLocalControlled => InternalOwnerId == EntityManager.InternalPlayerId;

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