using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);
    public delegate void ArrayBinding<TClass, TValue>(TClass obj, ReadOnlySpan<TValue> arr) where TValue : unmanaged;

    public readonly ref struct NotificationBinder
    {
        public unsafe void Bind<T, TEntity>(TEntity entity, ref SyncVarWithNotify<T> syncVar, Action<TEntity, T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            var classData = entity.EntityManager.ClassDataDict[entity.ClassId];
            if (syncVar.FieldId == 0)
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (classData.Fields[i].ChangeNotification)
                    {
                        ref var a = ref Utils.RefFieldValue<byte>(entity, classData.Fields[i].Offset);
                        a = (byte)(i+1);
                    }
                }
            }
            classData.Fields[syncVar.FieldId-1].OnSync = (ptr, buffer) =>
            {
                fixed(byte* data = buffer)
                    onChangedAction((TEntity)ptr, *(T*)data);
            };
        }
    }
    
    public abstract class InternalEntity : IComparable<InternalEntity>
    {
        /// <summary>
        /// Entity class id
        /// </summary>
        public readonly ushort ClassId;
        
        /// <summary>
        /// Entity instance id
        /// </summary>
        public readonly ushort Id;
        
        /// <summary>
        /// Entity manager
        /// </summary>
        public readonly EntityManager EntityManager;
        
        internal readonly byte Version;
        
        [SyncVar] 
        private SyncVarWithNotify<bool> _isDestroyed;
        
        /// <summary>
        /// Is entity is destroyed
        /// </summary>
        public bool IsDestroyed => _isDestroyed;

        /// <summary>
        /// Is entity is local controlled
        /// </summary>
        public bool IsLocalControlled => IsControlledBy(EntityManager.InternalPlayerId);
        
        /// <summary>
        /// Is entity is controlled by server
        /// </summary>
        public bool IsServerControlled => !IsControlledBy(EntityManager.InternalPlayerId);
        
        /// <summary>
        /// ClientEntityManager that available only on client. Will throw exception if called on server
        /// </summary>
        public ClientEntityManager ClientManager => (ClientEntityManager)EntityManager;
        
        /// <summary>
        /// ServerEntityManager that available only on server. Will throw exception if called on client
        /// </summary>
        public ServerEntityManager ServerManager => (ServerEntityManager)EntityManager;

        internal abstract bool IsControlledBy(byte playerId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref EntityClassData GetClassData()
        {
            return ref EntityManager.ClassDataDict[ClassId];
        }

        /// <summary>
        /// Is locally created entity
        /// </summary>
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        /// <summary>
        /// Destroy entity
        /// </summary>
        public void Destroy()
        {
            if ((EntityManager.IsClient && !IsLocal) || _isDestroyed)
                return;
            DestroyInternal();
        }

        /// <summary>
        /// Event called on entity destroy
        /// </summary>
        protected virtual void OnDestroy()
        {

        }
        
        internal virtual void DestroyInternal()
        {
            if (_isDestroyed)
                return;
            _isDestroyed = true;
            OnDestroy();
            EntityManager.RemoveEntity(this);
            if (EntityManager.IsServer && !IsLocal)
                ServerManager.DestroySavedData(this);
        }

        /// <summary>
        /// Fixed update. Called if entity has attribute <see cref="UpdateableEntity"/>
        /// </summary>
        public virtual void Update()
        {
        }

        /// <summary>
        /// Called only on <see cref="ClientEntityManager.Update"/> and if entity has attribute <see cref="UpdateableEntity"/>
        /// </summary>
        public virtual void VisualUpdate()
        {
            
        }

        internal void CallConstruct()
        {
            OnConstructed();
        }

        /// <summary>
        /// Called when entity constructed
        /// </summary>
        protected virtual void OnConstructed()
        {
        }

        public virtual void OnSyncStart()
        {
            
        }

        public virtual void OnSyncEnd()
        {
            
        }
        
        public virtual void BindChangeNotifications(in NotificationBinder binder)
        {
            binder.Bind(this, ref _isDestroyed, (entity, prevValue) =>
            {
                if (!prevValue && entity._isDestroyed)
                {
                    entity._isDestroyed = false;
                    entity.DestroyInternal();
                }
            });
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Id;
            ClassId = entityParams.ClassId;
            Version = entityParams.Version;
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        protected void CreateRPCAction(Action methodToCall, out Action cachedAction, ExecuteFlags flags = ExecuteFlags.None)
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");

            ref var classData = ref EntityManager.ClassDataDict[ClassId];
            byte rpcId = classData.RpcIdCounter;
            classData.RpcIdCounter++;
            
            if (EntityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = () => { methodToCall(); ServerManager.AddRemoteCall(Id, rpcId, flags); };
                else
                    cachedAction = () => ServerManager.AddRemoteCall(Id, rpcId, flags);
            }
            else
            {
                cachedAction = () =>
                {
                    if (IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall();
                };
            }
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        protected void CreateRPCAction<T>(Action<T> methodToCall, out Action<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            
            ref var classData = ref EntityManager.ClassDataDict[ClassId];
            byte rpcId = classData.RpcIdCounter;
            classData.RpcIdCounter++;

            if (EntityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = value => { methodToCall(value); ServerManager.AddRemoteCall(Id, value, rpcId, flags); };
                else
                    cachedAction = value => ServerManager.AddRemoteCall(Id, value, rpcId, flags);
            }
            else
            {
                cachedAction = value =>
                {
                    if (IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value);
                };
            }
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        protected void CreateRPCAction<T>(RemoteCallSpan<T> methodToCall, out RemoteCallSpan<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            
            ref var classData = ref EntityManager.ClassDataDict[ClassId];
            byte rpcId = classData.RpcIdCounter;
            classData.RpcIdCounter++;

            if (EntityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = value => { methodToCall(value); ServerManager.AddRemoteCall(Id, value, rpcId, flags); };
                else
                    cachedAction = value => ServerManager.AddRemoteCall(Id, value, rpcId, flags);
            }
            else
            {
                cachedAction = value =>
                {
                    if (IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value);
                };
            }
        }

        int IComparable<InternalEntity>.CompareTo(InternalEntity other)
        {
            //local first because mostly this is unity physics or something similar
            return (Id >= EntityManager.MaxSyncedEntityCount ? Id - ushort.MaxValue : Id) -
                   (other.Id >= EntityManager.MaxSyncedEntityCount ? other.Id - ushort.MaxValue : other.Id);
        }

        public override int GetHashCode()
        {
            return Id + Version * ushort.MaxValue;
        }

        public override string ToString()
        {
            return $"Entity. Id: {Id}, ClassId: {ClassId}, Version: {Version}";
        }
    }
}