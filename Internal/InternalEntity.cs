using System;
using System.Runtime.CompilerServices;

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
        public ClientEntityManager ClientManager => (ClientEntityManager)EntityManager;
        public ServerEntityManager ServerManager => (ServerEntityManager)EntityManager;

        internal abstract bool IsControlledBy(byte playerId);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe byte* GetPtr<T>(ref T entity) where T : InternalEntity
        {
            return (byte*)Unsafe.As<T, IntPtr>(ref entity);
        }

        /// <summary>
        /// Called when entity manager is resetted
        /// </summary>
        public virtual void Free()
        {
            
        }

        /// <summary>
        /// For debug purposes
        /// </summary>
        public virtual void DebugPrint()
        {
            
        }

        /// <summary>
        /// Fixed update. Called if entity has attribue <see cref="UpdateableEntity"/>
        /// </summary>
        public virtual void Update()
        {
        }

        /// <summary>
        /// Called only on <see cref="ClientEntityManager.Update"/> and if entity has attribue <see cref="UpdateableEntity"/>
        /// </summary>
        public virtual void VisualUpdate()
        {
            
        }

        /// <summary>
        /// Called when entity constructed
        /// </summary>
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

        int IComparable<InternalEntity>.CompareTo(InternalEntity other)
        {
            return Id - other.Id;
        }
    }
}