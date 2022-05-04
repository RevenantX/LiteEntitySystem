using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
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
            public bool IsLocalControlled => InternalIsLocalControlled;
            public bool IsServerControlled => !InternalIsLocalControlled;
            
            internal bool InternalIsLocalControlled;

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
                else if(InternalIsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
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
                else if(InternalIsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
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

    public abstract class EntityLogic : EntityManager.InternalEntity
    {
        [SyncVar(nameof(SetParentSync))] 
        private ushort _parentId;
        
        [SyncVar(nameof(DestroyedSync))] 
        private byte _isDestroyed;

        public bool IsDestroyed => _isDestroyed == 1;
        public readonly List<EntityLogic> Childs = new List<EntityLogic>();

        private bool _ownedEntity;

        internal void DestroyInternal()
        {
            _isDestroyed = 1;
            if(_ownedEntity)
                ((ClientEntityManager)EntityManager).OwnedEntities.Remove(this);
            EntityManager.RemoveEntity(this);
            OnDestroy();
            foreach (EntityLogic entityLogic in Childs)
            {
                entityLogic.DestroyInternal();
            }
        }

        private void DestroyedSync(byte prevValue)
        {
            if(_isDestroyed == 1)
                DestroyInternal();
        }

        protected virtual void OnDestroy()
        {

        }

        public void SetParent(EntityLogic parentEntity)
        {
            ushort id = parentEntity == null ? EntityManager.InvalidEntityId : parentEntity.Id;
            if (EntityManager.IsClient || id == _parentId)
                return;
            ushort oldId = _parentId;
            _parentId = id;
            SetParentSync(oldId);
        }

        private void SetParentSync(ushort oldId)
        {
            EntityManager.GetEntityById(oldId)?.Childs.Remove(this);
            var newParent = EntityManager.GetEntityById(_parentId);
            if (newParent != null)
            {
                newParent.Childs.Add(this);
                if (newParent.InternalIsLocalControlled != InternalIsLocalControlled)
                {
                    SetLocalControl(this, newParent.InternalIsLocalControlled);
                }
            }
        }

        internal void SetLocalControl(EntityLogic entity, bool localControl)
        {
            if (EntityManager.IsServer || entity.InternalIsLocalControlled == localControl)
                return;
            
            if (localControl)
            {
                _ownedEntity = true;
                ((ClientEntityManager)EntityManager).OwnedEntities.Add(this);
            }
            else if(_ownedEntity)
            {
                _ownedEntity = false;
                ((ClientEntityManager)EntityManager).OwnedEntities.Remove(this);
            }
            entity.InternalIsLocalControlled = localControl;
            foreach (EntityLogic child in Childs)
            {
                SetLocalControl(child, localControl);
            }
        }
        
        public T GetParent<T>() where T : EntityLogic
        {
            return _parentId == EntityManager.InvalidEntityId ? null : (T)EntityManager.GetEntityById(_parentId);
        }
        
        protected EntityLogic(EntityParams entityParams) : base(entityParams)
        {

        }
    }

    public abstract class SingletonEntityLogic : EntityManager.InternalEntity
    {
        protected SingletonEntityLogic(EntityParams entityParams) : base(entityParams)
        {
        }
    }

    [UpdateableEntity]
    public abstract class PawnLogic : EntityLogic
    {
        [SyncVar(nameof(OnControllerSync))] 
        private ControllerLogic _controller;

        public ControllerLogic Controller
        {
            get => _controller;
            internal set => _controller = value;
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams)
        {
           
        }

        private void OnControllerSync(ControllerLogic prev)
        {
            SetLocalControl(this, _controller != null && _controller.OwnerId == EntityManager.PlayerId);
        }

        public override void Update()
        {
            _controller?.BeforeControlledUpdate();
        }
    }
    
    public abstract class ControllerLogic : EntityManager.InternalEntity
    {
        [SyncVar(nameof(OnOwnerSync))] 
        private ushort _ownerId;
        
        [SyncVar] 
        private PawnLogic _controlledEntity;

        public ushort OwnerId
        {
            get => _ownerId;
            internal set => _ownerId = value;
        }
        
        public PawnLogic ControlledEntity => _controlledEntity;

        protected ControllerLogic(EntityParams entityParams) : base(entityParams)
        {
           
        }

        private void OnOwnerSync(ushort prevOwner)
        {
            InternalIsLocalControlled = _ownerId == EntityManager.PlayerId;
        }

        public virtual void BeforeControlledUpdate()
        {
            
        }

        public void StartControl<T>(T target) where T : PawnLogic
        {
            _controlledEntity = target;
            target.Controller = this;
            EntityManager.GetEntities<T>().OnRemoved +=
                e =>
                {
                    if (e == _controlledEntity)
                        StopControl();
                };
        }

        public void StopControl()
        {
            _controlledEntity.Controller = null;
            _controlledEntity = null;
        }
    }

    public abstract class ControllerLogic<T> : ControllerLogic where T : PawnLogic
    {
        public new T ControlledEntity => (T) base.ControlledEntity;

        protected ControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    [ServerOnly]
    public abstract class AiControllerLogic<T> : ControllerLogic<T> where T : PawnLogic
    {
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