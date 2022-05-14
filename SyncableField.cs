using System;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [AttributeUsage(AttributeTargets.Method)]
    public class SyncableRemoteCall : Attribute
    {
        internal byte Id = byte.MaxValue;
        internal int DataSize;
        internal MethodCallDelegate MethodDelegate;
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

        protected void ExecuteOnClient(Action methodToCall)
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            EntityManager?.AddSyncableCall(this, methodToCall.Method);
        }

        protected void ExecuteOnClient<T>(Action<T> methodToCall, T value) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            EntityManager?.AddSyncableCall(this, value, methodToCall.Method);
        }
        
        protected void ExecuteOnClient<T>(Action<T[]> methodToCall, T[] value, int count) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            EntityManager?.AddSyncableCall(this, value, count, methodToCall.Method);
        }
    }
}