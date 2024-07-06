using System;
using System.Runtime.CompilerServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal InternalEntity ParentEntityInternal;
        internal ExecuteFlags Flags;
        internal ushort RPCOffset;
        
        internal ServerEntityManager ServerEntityManager => ParentEntityInternal?.EntityManager as ServerEntityManager;

        protected bool IsServer => ParentEntityInternal != null && ParentEntityInternal.EntityManager.IsServer;
        protected bool IsClient => !IsServer;

        protected internal virtual void OnSyncRequested()
        {
            
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc) => rpc.Call(this);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged => rpc.Call(this, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged => rpc.Call(this, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T1, T2>(in RemoteCall<T1, T2> rpc, T1 value1, ReadOnlySpan<T2> value2) 
            where T1 : unmanaged 
            where T2 : unmanaged => 
            rpc.Call(this, value1, value2);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T1, T2>(in RemoteCallSpan<T1, T2> rpc, ReadOnlySpan<T1> value1, ReadOnlySpan<T2> value2)      
            where T1 : unmanaged 
            where T2 : unmanaged => 
            rpc.Call(this, value1, value2);
    }
}