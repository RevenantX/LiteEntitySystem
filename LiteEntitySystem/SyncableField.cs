using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal ushort ParentEntityId = EntityManager.InvalidEntityId;
        internal ExecuteFlags Flags;

        internal void InternalInit(in SyncableRPCRegistrator r)
        {
            RegisterRPC(in r);
        }

        internal void InternalOnSyncRequested()
        {
            OnSyncRequested();
        }

        protected virtual void OnSyncRequested()
        {
            
        }

        protected virtual void RegisterRPC(in SyncableRPCRegistrator r)
        {

        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            ((Action<SyncableField>)rpc.CachedAction)?.Invoke(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            ((Action<SyncableField, T>)rpc.CachedAction)?.Invoke(this, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            ((SpanAction<SyncableField, T>)rpc.CachedAction)?.Invoke(this, value);
        }
    }
}