using System;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    
    /// <summary>
    /// Provides a standard interface for receiving SyncVar change notifications.
    /// </summary>
    public interface ISyncFieldChanged<T>
    {
        event Action<T, T> ValueChanged;
    }
    
    public abstract class SyncableField : InternalBaseClass
    {
        internal InternalEntity ParentEntityInternal;
        internal ExecuteFlags Flags;
        internal ushort RPCOffset;
        
        /// <summary>
        /// Is syncableField on client
        /// </summary>
        protected internal bool IsClient => !IsServer;
        
        /// <summary>
        /// Is syncableField on server
        /// </summary>
        protected internal bool IsServer => ParentEntityInternal != null && ParentEntityInternal.IsServer;

        /// <summary>
        /// Is supported rollback by this syncable field
        /// </summary>
        public virtual bool IsRollbackSupported => false;

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
                ParentEntityInternal.ServerManager.AddRemoteCall(ParentEntityInternal, (ushort)(rpc.Id + RPCOffset), rpc.Flags);
        }

        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            unsafe
            {
                if(IsServer)
                    ParentEntityInternal.ServerManager.AddRemoteCall(ParentEntityInternal, new ReadOnlySpan<T>(&value, 1), (ushort)(rpc.Id + RPCOffset), Flags);
            }
        }

        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if(IsServer)
                ParentEntityInternal.ServerManager.AddRemoteCall(ParentEntityInternal, value, (ushort)(rpc.Id + RPCOffset), Flags);
        }

        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable
        {
            if (IsServer)
            {
                var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                value.Serialize(ref writer);
                ParentEntityInternal.ServerManager.AddRemoteCall<byte>(ParentEntityInternal, writer.RawData.Slice(0, writer.Position), (ushort)(rpc.Id + RPCOffset), Flags);
            }
        }
    }
}