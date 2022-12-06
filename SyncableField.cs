using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal byte FieldId;
        private bool _isRegistered;

        public virtual void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void FullSyncRead(ReadOnlySpan<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            _isRegistered = true;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if (!_isRegistered)
                return;
            ((Action<SyncableField>)rpc.CachedAction)(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            if (!_isRegistered)
                return;
            ((Action<SyncableField, T>)rpc.CachedAction)(this, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if (!_isRegistered)
                return;
            ((SpanAction<SyncableField, T>)rpc.CachedAction)(this, value);
        }
    }
}