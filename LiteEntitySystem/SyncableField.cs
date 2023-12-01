using System;
using System.Runtime.CompilerServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public abstract class SyncableField : InternalSyncType
    {
        internal InternalEntity ParentEntity;
        internal ushort SyncableId;
        
        protected internal virtual void OnSyncRequested()
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