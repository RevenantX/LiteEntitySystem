using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public abstract class InternalEntity : InternalBaseClass, IComparable<InternalEntity>
    {
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        internal SyncVar<byte> InternalOwnerId;

        internal byte[] IOBuffer;

        internal readonly int UpdateOrderNum;

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

        /// <summary>
        /// Is entity on server
        /// </summary>
        public bool IsServer => EntityManager.IsServer;

        /// <summary>
        /// Is entity on server
        /// </summary>
        public bool IsClient => EntityManager.IsClient;

        /// <summary>
        /// Entity version (for id reuse)
        /// </summary>
        public readonly byte Version;

        internal EntityDataHeader DataHeader => new EntityDataHeader
        (
            Id,
            ClassId,
            Version,
            UpdateOrderNum
        );

        [SyncVarFlags(SyncFlags.NeverRollBack)]
        private SyncVar<bool> _isDestroyed;

        /// <summary>
        /// Is entity is destroyed
        /// </summary>
        public bool IsDestroyed => _isDestroyed;

        /// <summary>
        /// Is entity local controlled
        /// </summary>
        public bool IsLocalControlled => InternalOwnerId.Value == EntityManager.InternalPlayerId;

        /// <summary>
        /// Is entity remote controlled
        /// </summary>
        public bool IsRemoteControlled => InternalOwnerId.Value != EntityManager.InternalPlayerId;

        /// <summary>
        /// Is entity is controlled by server
        /// </summary>
        public bool IsServerControlled => InternalOwnerId.Value == EntityManager.ServerPlayerId;

        /// <summary>
        /// ClientEntityManager that available only on client. Will throw exception if called on server
        /// </summary>
        public ClientEntityManager ClientManager => (ClientEntityManager)EntityManager;

        /// <summary>
        /// ServerEntityManager that available only on server. Will throw exception if called on client
        /// </summary>
        public ServerEntityManager ServerManager => (ServerEntityManager)EntityManager;

        /// <summary>
        /// Owner player id
        /// ServerPlayerId - 0
        /// Singletons always controlled by server
        /// </summary>
        public byte OwnerId => InternalOwnerId.Value;

        /// <summary>
        /// Is locally created entity
        /// </summary>
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        /// <summary>
        /// Is entity based on SingletonEntityLogic
        /// </summary>
        public bool IsSingleton => ClassData.IsSingleton;

        internal ref EntityClassData ClassData => ref EntityManager.ClassDataDict[ClassId];

        /// <summary>
        /// Is entity released and not used after destroy.
        /// </summary>
        public bool IsRemoved { get; internal set; }

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
                _isDestroyed.Value = false;
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
            _isDestroyed.Value = true;
            EntityManager.OnEntityDestroyed(this);
            OnDestroy();
        }

        internal void SafeUpdate()
        {
            try
            {
                Update();
            }
            catch (Exception e)
            {
                Logger.LogError($"Exception in entity({Id}) update:\n{e}");
            }
        }

        /// <summary>
        /// Fixed update. Called if entity has attribute <see cref="EntityFlagsAttribute"/> and flag Updateable
        /// </summary>
        protected internal virtual void Update()
        {
        }

        /// <summary>
        /// Called at rollback begin before all values reset to first frame in rollback queue.
        /// </summary>
        protected internal virtual void OnBeforeRollback()
        {

        }

        /// <summary>
        /// Called at rollback begin after all values reset to first frame in rollback queue.
        /// </summary>
        protected internal virtual void OnRollback()
        {

        }

        /// <summary>
        /// Called only on <see cref="ClientEntityManager.Update"/> and if entity has attribute <see cref="EntityFlagsAttribute"/> and flag Updateable
        /// </summary>
        protected internal virtual void VisualUpdate()
        {

        }

        /// <summary>
        /// Called when entity constructed
        /// </summary>
        protected internal virtual void OnConstructed()
        {
        }

        internal void RegisterRpcInternal()
        {
            ref var classData = ref EntityManager.ClassDataDict[ClassId];

            // Setup SyncVar<T> onChange bindings
            var onChangeTarget = EntityManager.IsServer && !IsLocal ? this : null;
            for (int i = 0; i < classData.FieldsCount; i++)
            {
                ref var field = ref classData.Fields[i];

                // SyncVar inside a syncable field (first-level only)
                if (field.FieldType == FieldType.SyncVar)
                {
                    field.TypeProcessor.InitSyncVar(this, field.Offsets.Last(), onChangeTarget, (ushort)i);
                }
                else
                {
                    // find SyncVar via offset map
                    InternalBaseClass syncable = this;
                    for (int j = 0; j < field.Offsets.Length-1; j++)
                    {
                        syncable = RefMagic.RefFieldValue<SyncableField>(syncable, field.Offsets[j]);
                        if (syncable == null)
                            throw new NullReferenceException($"SyncVar at offset {field.Offsets[j]} is null");
                    }

                    field.TypeProcessor.InitSyncVar(syncable, field.Offsets.Last(), onChangeTarget, (ushort)i);
                }
            }

            // Register top-level RPCs if not yet cached
            List<RpcFieldInfo> rpcCache = null;
            if (classData.RemoteCallsClient == null)
            {
                rpcCache = new List<RpcFieldInfo>();
                var rpcRegistrator = new RPCRegistrator(rpcCache, classData.Fields);
                RegisterRPC(ref rpcRegistrator);
                Logger.Log($"RegisterRPCs for class: {classData.ClassId}");
            }

            // Setup SyncableField RPC offsets and bindings with nested traversal
            for (int i = 0; i < classData.SyncableFields.Length; i++)
            {
                ref var syncInfo = ref classData.SyncableFields[i];

                // Traverse the chain of nested SyncableFields via offset map
                object current = this;
                foreach (var relOffset in syncInfo.Offsets)
                {
                    current = RefMagic.RefFieldValue<SyncableField>(current, relOffset);
                    if (current == null)
                        throw new NullReferenceException($"Nested SyncableField at offset {relOffset} is null");
                }
                var syncField = (SyncableField)current;
                syncField.ParentEntityInternal = this;

                // Apply flag-based RPC targeting
                if (syncInfo.Flags.HasFlagFast(SyncFlags.OnlyForOwner))
                    syncField.Flags = ExecuteFlags.SendToOwner;
                else if (syncInfo.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                    syncField.Flags = ExecuteFlags.SendToOther;
                else
                    syncField.Flags = ExecuteFlags.SendToAll;

                // Assign or register RPC offsets
                if (classData.RemoteCallsClient != null)
                {
                    // Use cached offsets
                    syncField.RPCOffset = syncInfo.RPCOffset;
                }
                else
                {
                    // New registration
                    syncField.RPCOffset = (ushort)rpcCache.Count;
                    syncInfo.RPCOffset = syncField.RPCOffset;
                    // Use AbsoluteOffset to register RPCs on this nested SyncableField
                    var syncRpcRegistrator = new SyncableRPCRegistrator(syncInfo.Offsets, rpcCache);
                    syncField.RegisterRPC(ref syncRpcRegistrator);
                    Logger.Log($"RegisterSyncableRPCs for class: {classData.ClassId}");
                }
            }

            // Cache the RPC list if newly created
            if (classData.RemoteCallsClient == null)
                classData.RemoteCallsClient = rpcCache.ToArray();
        }




        /// <summary>
        /// Method for registering RPCs and OnChange notifications
        /// </summary>
        /// <param name="r"></param>
        protected virtual void RegisterRPC(ref RPCRegistrator r)
        {
            r.BindOnChange(this, ref _isDestroyed, OnDestroyChange);
        }

        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if (IsRemoved)
                return;
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this);
                ServerManager.AddRemoteCall(this, rpc.Id, rpc.Flags);
            }
            else if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                rpc.CachedAction(this);
        }

        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            if (IsRemoved)
                return;
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                unsafe
                {
                    ServerManager.AddRemoteCall(this, new ReadOnlySpan<T>(&value, 1), rpc.Id, rpc.Flags);
                }
            }
            else if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                rpc.CachedAction(this, value);
        }

        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if (IsRemoved)
                return;
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                ServerManager.AddRemoteCall(this, value, rpc.Id, rpc.Flags);
            }
            else if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                rpc.CachedAction(this, value);
        }

        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable
        {
            if (IsRemoved)
                return;
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                value.Serialize(ref writer);
                ServerManager.AddRemoteCall<byte>(this, writer.RawData.Slice(0, writer.Position), rpc.Id, rpc.Flags);
            }
            else if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                rpc.CachedAction(this, value);
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Header.Id;
            ClassId = entityParams.Header.ClassId;
            Version = entityParams.Header.Version;
            UpdateOrderNum = entityParams.Header.UpdateOrder;
            IOBuffer = entityParams.IOBuffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(InternalEntity other) => UpdateOrderNum != other.UpdateOrderNum ? UpdateOrderNum - other.UpdateOrderNum : Id - other.Id;

        public override int GetHashCode() => UpdateOrderNum;

        public override string ToString() =>
            $"Entity. Id: {Id}, ClassId: {ClassId}, Version: {Version}";
    }
}