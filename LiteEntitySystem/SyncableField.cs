using System;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Provides a standard interface for receiving SyncVar change notifications.
    /// </summary>
    public interface ISyncFieldChanged<T>
    {
        /// <summary>
        /// Argument contains old value
        /// </summary>
        event Action<T> ValueChanged;
    }
    
    /// <summary>
    /// Provides a standard interface for receiving SyncVar change notifications.
    /// </summary>
    public interface ISyncFieldChanged
    {
        event Action ValueChanged;
    }
    
    /// <summary>
    /// Base class for fields with custom serialization (strings,lists,etc)
    /// </summary>
    public abstract class SyncableField : InternalBaseClass
    {
        private InternalEntity _parentEntity;
        private ExecuteFlags _executeFlags;
        
        internal ushort RPCOffset;
        
        /// <summary>
        /// Is syncableField on client
        /// </summary>
        protected internal bool IsClient => !IsServer;
        
        /// <summary>
        /// Is syncableField on server
        /// </summary>
        protected internal bool IsServer => _parentEntity != null && _parentEntity.IsServer;

        /// <summary>
        /// Owner of this syncable field
        /// </summary>
        protected InternalEntity ParentEntity => _parentEntity;

        internal void Init(InternalEntity parentEntity, SyncFlags fieldFlags)
        {
            _parentEntity = parentEntity;
            if (fieldFlags.HasFlagFast(SyncFlags.OnlyForOwner))
                _executeFlags = ExecuteFlags.SendToOwner;
            else if (fieldFlags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                _executeFlags = ExecuteFlags.SendToOther;
            else
                _executeFlags = ExecuteFlags.SendToAll;
        }
        
        /// <summary>
        /// Owner of this syncable field casted to EntityLogic
        /// </summary>
        protected EntityLogic ParentEntityLogic => _parentEntity as EntityLogic;

        protected internal virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {

        }
        
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if(IsServer)
                _parentEntity.ServerManager.AddRemoteCall(_parentEntity, (ushort)(rpc.Id + RPCOffset), _executeFlags);
        }

        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            unsafe
            {
                if(IsServer)
                    _parentEntity.ServerManager.AddRemoteCall(_parentEntity, new ReadOnlySpan<T>(&value, 1), (ushort)(rpc.Id + RPCOffset), _executeFlags);
            }
        }

        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if(IsServer)
                _parentEntity.ServerManager.AddRemoteCall(_parentEntity, value, (ushort)(rpc.Id + RPCOffset), _executeFlags);
        }

        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable
        {
            if (IsServer)
            {
                var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                value.Serialize(ref writer);
                _parentEntity.ServerManager.AddRemoteCall<byte>(_parentEntity, writer.RawData.Slice(0, writer.Position), (ushort)(rpc.Id + RPCOffset), _executeFlags);
            }
        }
    }

    /// <summary>
    /// Syncable fields with custom rollback notifications and implementation
    /// </summary>
    public abstract class SyncableFieldCustomRollback : SyncableField
    {
        /// <summary>
        /// Marks that SyncableField was modified on client and add parent entity to Rollback list
        /// </summary>
        protected void MarkAsChanged()
        {
            if(ParentEntity != null && ParentEntity.IsClient)
                ParentEntity.ClientManager.MarkEntityChanged(ParentEntity);
        }
        
        protected internal abstract void BeforeReadRPC();

        protected internal abstract void AfterReadRPC();

        protected internal abstract void OnRollback();
    }
}