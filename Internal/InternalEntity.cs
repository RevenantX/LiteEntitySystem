using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
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
        
        private void OnDestroyChange(bool prevValue)
        {
            if (!prevValue && _isDestroyed)
            {
                _isDestroyed = false;
                DestroyInternal();
            }
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

        internal void RegisterRpcInternal()
        {
            ref var classData = ref GetClassData();
            //load cache and/or init RpcIds
            for (int i = 0; i < classData.RpcOffsets.Length; i++)
            {
                ref var remoteCall = ref Utils.RefFieldValue<RemoteCall>(this, classData.RpcOffsets[i]);
                remoteCall = new RemoteCall((byte)i, classData.RPCCache[i]);
            }
            for (int i = 0; i < classData.SyncableRpcOffsets.Length; i++)
            {
                var syncable = Utils.RefFieldValue<SyncableField>(this, classData.SyncableRpcOffsets[i].SyncableOffset);
                ref var remoteCall = ref Utils.RefFieldValue<RemoteCall>(syncable, classData.SyncableRpcOffsets[i].RpcOffset);
                remoteCall = new RemoteCall((byte)i, classData.SyncableRPCCache[i]);
                syncable.ParentEntityId = Id;
            }
            if(!classData.IsRpcBound)
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var field = ref classData.Fields[i];
                    if (field.FieldType == FieldType.SyncVarWithNotification)
                    {
                        ref var a = ref Utils.RefFieldValue<byte>(this, field.Offset + field.IntSize);
                        a = (byte)i;
                    }
                }
                var r = new RPCRegistrator();
                RegisterRPC(ref r);
                for (int i = 0; i < classData.SyncableFieldOffsets.Length; i++)
                {
                    var syncable = Utils.RefFieldValue<SyncableField>(this, classData.SyncableFieldOffsets[i]);
                    syncable.FieldId = (byte)i;
                    var syncableRegistrator = new SyncableRPCRegistrator(this);
                    syncable.RegisterRPC(ref syncableRegistrator);
                }
                classData.IsRpcBound = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            ((Action<InternalEntity>)rpc.CachedAction)(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            ((Action<InternalEntity, T>)rpc.CachedAction)(this, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            ((SpanAction<InternalEntity, T>)rpc.CachedAction)(this, value);
        }

        protected virtual void RegisterRPC(ref RPCRegistrator r)
        {
            r.BindOnChange(this, ref _isDestroyed, OnDestroyChange);
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Id;
            ClassId = entityParams.ClassId;
            Version = entityParams.Version;
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