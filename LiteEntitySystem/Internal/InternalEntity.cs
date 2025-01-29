using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib.Utils;

namespace LiteEntitySystem.Internal
{
    public readonly struct EntityDataHeader
    {
        public readonly ushort Id;
        public readonly ushort ClassId;
        public readonly byte Version;
        public readonly int UpdateOrder;

        public EntityDataHeader(ushort id, ushort classId, byte version, int updateOrder)
        {
            Id = id;
            ClassId = classId;
            Version = version;
            UpdateOrder = updateOrder;
        }
    }

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

            //setup field ids for BindOnChange and pass on server this for OnChangedEvent to StateSerializer
            var onChangeTarget = EntityManager.IsServer && !IsLocal ? this : null;
            for (int i = 0; i < classData.FieldsCount; i++)
            {
                ref var field = ref classData.Fields[i];
                if (field.FieldType == FieldType.SyncVar)
                {
                    field.TypeProcessor.InitSyncVar(this, field.Offset, onChangeTarget, (ushort)i);
                }
                else
                {
                    var syncableField = RefMagic.RefFieldValue<SyncableField>(this, field.Offset);
                    field.TypeProcessor.InitSyncVar(syncableField, field.SyncableSyncVarOffset, onChangeTarget,
                        (ushort)i);
                }
            }

            List<RpcFieldInfo> rpcCahce = null;
            if (classData.RemoteCallsClient == null)
            {
                rpcCahce = new List<RpcFieldInfo>();
                var rpcRegistrator = new RPCRegistrator(rpcCahce, classData.Fields);
                RegisterRPC(ref rpcRegistrator);
                //Logger.Log($"RegisterRPCs for class: {classData.ClassId}");
            }

            //setup id for later sync calls
            for (int i = 0; i < classData.SyncableFields.Length; i++)
            {
                ref var syncFieldInfo = ref classData.SyncableFields[i];
                var syncField = RefMagic.RefFieldValue<SyncableField>(this, syncFieldInfo.Offset);
                syncField.ParentEntityInternal = this;
                if (syncFieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForOwner))
                    syncField.Flags = ExecuteFlags.SendToOwner;
                else if (syncFieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                    syncField.Flags = ExecuteFlags.SendToOther;
                else
                    syncField.Flags = ExecuteFlags.SendToAll;
                if (classData.RemoteCallsClient != null)
                {
                    syncField.RPCOffset = syncFieldInfo.RPCOffset;
                }
                else
                {
                    syncField.RPCOffset = (ushort)rpcCahce.Count;
                    syncFieldInfo.RPCOffset = syncField.RPCOffset;
                    var syncablesRegistrator = new SyncableRPCRegistrator(syncFieldInfo.Offset, rpcCahce);
                    syncField.RegisterRPC(ref syncablesRegistrator);
                }
            }

            classData.RemoteCallsClient ??= rpcCahce.ToArray();
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
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                unsafe
                {
                    ServerManager.AddRemoteCall(this, new ReadOnlySpan<T>(&value, 1), rpc.Id, rpc.Flags);
                }
            }
            else // we are a client
            {
                // check if we want to run local prediction
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                {
                    rpc.CachedAction(this, value);
                }

                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                {
                    if (EntityManager is ClientEntityManager cem)
                    {
                        unsafe
                        {
                            cem.SendServerRPC(Id, rpc.Id, new ReadOnlySpan<T>(&value, 1));
                        }
                    }
                }
            }
        }

        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                ServerManager.AddRemoteCall(this, value, rpc.Id, rpc.Flags);
            }
            else
            {
                // If client and ExecuteOnPrediction + local-controlled, call locally
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                    rpc.CachedAction(this, value);

                // If this call is meant to execute on server...
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                {
                    // ... we manually serialize and send to the server
                    if (EntityManager is ClientEntityManager cem)
                    {
                        // Send the serialized bytes to the server
                        cem.SendServerRPC(
                            Id,
                            rpc.Id,
                            value
                        );
                    }
                }
            }
        }

        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable
        {
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);
                var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                value.Serialize(ref writer);
                ServerManager.AddRemoteCall<byte>(this, writer.RawData.Slice(0, writer.Position), rpc.Id, rpc.Flags);
            }
            else
            {
                // If client and ExecuteOnPrediction + local-controlled, call locally
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                    rpc.CachedAction(this, value);

                // If this call is meant to execute on server...
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                {
                    // ... we manually serialize and send to the server
                    if (EntityManager is ClientEntityManager cem)
                    {
                        SpanWriter writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                        value.Serialize(ref writer);

                        // Send the serialized bytes to the server
                        cem.SendServerRPC(
                            Id,
                            rpc.Id,
                            writer.RawData.Slice(0, writer.Position)
                        );
                    }
                }
            }
        }


        protected void ExecuteRPC<T>(in RemoteCallNetSerializable<T> rpc, T value) where T : INetSerializable, new()
        {
            if (IsServer)
            {
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    rpc.CachedAction(this, value);

                var writer = new NetDataWriter();
                value.Serialize(writer);
                ServerManager.AddRemoteCall<byte>(this, writer.Data, rpc.Id, rpc.Flags);
            }
            else
            {
                // If client and ExecuteOnPrediction + local-controlled, call locally
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && IsLocalControlled)
                    rpc.CachedAction(this, value);

                // If this call is meant to execute on server...
                if (rpc.Flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                {
                    // ... we manually serialize and send to the server
                    if (EntityManager is ClientEntityManager cem)
                    {
                        var writer = new NetDataWriter();
                        value.Serialize(writer);

                        // Send the serialized bytes to the server
                        cem.SendServerRPC(
                            Id,
                            rpc.Id,
                            writer.CopyData()
                        );
                    }
                }
            }
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
        public int CompareTo(InternalEntity other) => UpdateOrderNum != other.UpdateOrderNum
            ? UpdateOrderNum - other.UpdateOrderNum
            : Id - other.Id;

        public override int GetHashCode() => UpdateOrderNum;

        public override string ToString() =>
            $"Entity. Id: {Id}, ClassId: {ClassId}, Version: {Version}";
    }
}