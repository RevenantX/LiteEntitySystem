using System;
using System.Runtime.CompilerServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public abstract class SyncableField : InternalSyncType
    {
        internal InternalEntity ParentEntity;
        internal ushort RpcOffset;
        internal ExecuteFlags Flags;

        protected internal virtual void OnSyncRequested()
        {
            
        }

        protected internal virtual void RegisterRPC(in SyncableRPCRegistrator r)
        {

        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            rpc.CachedActionServer?.Invoke(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            rpc.CachedActionServer?.Invoke(this, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            rpc.CachedActionServer?.Invoke(this, value);
        }
    }
}