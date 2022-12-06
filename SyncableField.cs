using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal ushort ParentEntityId;
        internal byte FieldId;

        protected SyncableField()
        {
            ParentEntityId = EntityManager.InvalidEntityId;
        }

        public virtual void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void FullSyncRead(ReadOnlySpan<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void RegisterRPC(in SyncableRPCRegistrator r)
        {

        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC(in RemoteCall rpc)
        {
            if (ParentEntityId != EntityManager.InvalidEntityId)
                ((Action<SyncableField>)rpc.CachedAction)(this);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCall<T> rpc, T value) where T : unmanaged
        {
            if (ParentEntityId != EntityManager.InvalidEntityId)
                ((Action<SyncableField, T>)rpc.CachedAction)(this, value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void ExecuteRPC<T>(in RemoteCallSpan<T> rpc, ReadOnlySpan<T> value) where T : unmanaged
        {
            if (ParentEntityId != EntityManager.InvalidEntityId)
                ((SpanAction<SyncableField, T>)rpc.CachedAction)(this, value);
        }
    }
}