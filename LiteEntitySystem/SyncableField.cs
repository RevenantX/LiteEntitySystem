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
        
        internal int FieldId;
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
        /// Is supported rollback by this syncable field
        /// </summary>
        public virtual bool IsRollbackSupported => false;

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
        
        protected internal virtual void BeforeReadRPC()
        {
            
        }

        protected internal virtual void AfterReadRPC()
        {
            
        }

        protected internal virtual void OnRollback()
        {
            
        }

        protected internal virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {

        }
        
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if(IsServer)
                _parentEntity.ServerManager.AddRemoteCall(_parentEntity, (ushort)(rpc.Id + RPCOffset), rpc.Flags);
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
}