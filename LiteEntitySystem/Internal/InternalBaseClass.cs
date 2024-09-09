using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public abstract class InternalBaseClass
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc) => rpc.Call(this);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged => rpc.Call(this, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged => rpc.Call(this, value);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSerializable<T> rpc, T value) where T : struct, ISpanSerializable => rpc.Call(this, value);
        
        protected internal virtual void OnSyncRequested()
        {
            
        }
    }
}