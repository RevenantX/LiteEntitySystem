using System;

namespace LiteEntitySystem.Internal
{
    public abstract class InternalEntity : IComparable<InternalEntity>
    {
        public readonly ushort ClassId;
        public readonly ushort Id;
        public readonly EntityManager EntityManager;
        public readonly byte Version;
        public abstract bool IsLocalControlled { get; }
        public bool IsServerControlled => !IsLocalControlled;
        
        internal abstract bool IsControlledBy(byte playerId);

        public virtual void DebugPrint()
        {
            
        }

        public virtual void Update()
        {
        }

        public virtual void OnConstructed()
        {
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Id;
            ClassId = entityParams.ClassId;
            Version = entityParams.Version;
        }

        protected void ExecuteRemoteCall<T>(Action<T> methodToCall, T value) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            var classData = EntityManager.ClassDataDict[ClassId];
            if(!classData.RemoteCalls.TryGetValue(methodToCall.Method, out var remoteCallInfo))
                throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");
            if (EntityManager.IsServer)
            {
                if ((remoteCallInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                    methodToCall(value);
                ((ServerEntityManager)EntityManager).AddRemoteCall(Id, value, remoteCallInfo);
            }
            else if(IsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
            {
                methodToCall(value);
            }
        }
        
        protected void ExecuteRemoteCall<T>(Action<T[]> methodToCall, T[] value, int count) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            var classData = EntityManager.ClassDataDict[ClassId];
            if(!classData.RemoteCalls.TryGetValue(methodToCall.Method, out var remoteCallInfo))
                throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");
            if (EntityManager.IsServer)
            {
                if ((remoteCallInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                    methodToCall(value);
                ((ServerEntityManager)EntityManager).AddRemoteCall(Id, value, count, remoteCallInfo);
            }
            else if(IsLocalControlled && (remoteCallInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
            {
                methodToCall(value);
            }
        }

        public int CompareTo(InternalEntity other)
        {
            return Id - other.Id;
        }
    }
}