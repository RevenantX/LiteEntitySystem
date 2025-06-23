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
            ClassId,
            Version,
            UpdateOrderNum
        );
        
        /// <summary>
        /// Is entity is destroyed
        /// </summary>
        public bool IsDestroyed { get; private set; }

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
        /// Is entity constructed (OnConstruct called)
        /// </summary>
        public bool IsConstructed { get; internal set; }

        /// <summary>
        /// Is entity released and not used after destroy.
        /// </summary>
        public bool IsRemoved { get; internal set; }

        /// <summary>
        /// Destroy entity
        /// </summary>
        public void Destroy()
        {
            if (EntityManager.IsClient && !IsLocal)
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
            if (IsDestroyed)
                return;
            IsDestroyed = true;
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
        
        /// <summary>
        /// Called when entity constructed but at end of frame
        /// </summary>
        protected internal virtual void OnLateConstructed()
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

                field.TypeProcessor.InitSyncVar(this, field.Offsets, onChangeTarget, (ushort)i);
            }

            // Register top-level RPCs if not yet cached
            List<RpcFieldInfo> rpcCache = null;
            if (classData.RemoteCallsClient == null)
            {
                rpcCache = new List<RpcFieldInfo>();
                //place reserved rpcs
                RemoteCallPacket.InitReservedRPCs(rpcCache);

                var rpcRegistrator = new RPCRegistrator(rpcCache, classData.Fields);
                RegisterRPC(ref rpcRegistrator);
                // Logger.Log($"RegisterRPCs for class: {classData.ClassId}");
            }

            // Setup id for later sync calls
            for (int i = 0; i < classData.SyncableFields.Length; i++)
            {
                ref var syncFieldInfo = ref classData.SyncableFields[i];

                // Traverse the chain of nested SyncableFields via offset map
                var syncField = Utils.GetSyncableField(this, syncFieldInfo.Offsets);
                

                syncField.Init(this, syncFieldInfo.Flags);
                if (classData.RemoteCallsClient != null) //rpcCache == null
                {
                    // Use cached offsets
                    syncField.RPCOffset = syncFieldInfo.RPCOffset;
                }
                else
                {
                    // New registration
                    syncField.RPCOffset = (ushort)rpcCache.Count;
                    syncFieldInfo.RPCOffset = syncField.RPCOffset;

                    // Use AbsoluteOffset to register RPCs on this nested SyncableField
                    var syncablesRegistrator = new SyncableRPCRegistrator(syncFieldInfo.Offsets, rpcCache);
                    syncField.RegisterRPC(ref syncablesRegistrator);
                }
            }
            classData.RemoteCallsClient ??= rpcCache.ToArray();
        }




        /// <summary>
        /// Method for registering RPCs and OnChange notifications
        /// </summary>
        /// <param name="r"></param>
        protected virtual void RegisterRPC(ref RPCRegistrator r)
        {

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
            Id = entityParams.Id;
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