using System;

namespace LiteEntitySystem
{
    [AttributeUsage(AttributeTargets.Method)]
    public class SyncableRemoteCall : Attribute
    {
        internal byte Id = byte.MaxValue;
        internal int DataSize;
    }
    
    public abstract class SyncableField
    {
        //This setups in Serializer.Init
        internal ServerEntityManager EntityManager;
        internal byte FieldId;
        internal ushort EntityId;

        public bool IsClient => EntityManager == null;
        public bool IsServer => EntityManager != null;
        
        public abstract unsafe void FullSyncWrite(byte* data, ref int position);
        public abstract unsafe void FullSyncRead(byte* data, ref int position);
        public abstract void OnServerInitialized();

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