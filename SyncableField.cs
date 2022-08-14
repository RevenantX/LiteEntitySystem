using System;

namespace LiteEntitySystem
{
    [AttributeUsage(AttributeTargets.Method)]
    public class SyncableRemoteCall : Attribute
    {
        internal byte Id = byte.MaxValue;
        internal int DataSize;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class SyncableSyncVar : Attribute
    {
        
    }
    
    public abstract class SyncableField
    {
        //This setups in Serializer.Init
        internal ServerEntityManager EntityManager;
        internal byte FieldId;
        internal ushort EntityId;

        protected bool IsClient => EntityManager == null;
        protected bool IsServer => EntityManager != null;

        public virtual void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void FullSyncRead(Span<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void OnServerInitialized()
        {
            
        }

        protected void CreateClientAction(Action methodToCall, out Action cachedAction)
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            cachedAction = () => EntityManager?.AddSyncableCall(this, methodToCall.Method);
        }

        protected void CreateClientAction<T>(Action<T> methodToCall, out Action<T> cachedAction) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            cachedAction = value => EntityManager?.AddSyncableCall(this, value, methodToCall.Method);
        }
        
        protected void CreateClientAction<T>(Action<T[], ushort> methodToCall, out Action<T[], ushort> cachedAction) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            cachedAction = (value, count) => EntityManager?.AddSyncableCall(this, value, count, methodToCall.Method);
        }
    }
}