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
        internal ref EntityClassData GetClassData()
        {
            return ref EntityManager.ClassDataDict[ClassId];
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe byte* GetPtr<T>(ref T entity) where T : InternalEntity
        {
            return (byte*)Unsafe.As<T, IntPtr>(ref entity);
        }

        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        /// <summary>
        /// Called when entity manager is reset
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
        /// Fixed update. Called if entity has attribute <see cref="UpdateableEntity"/>
        /// </summary>
        public virtual void Update()
        {
        }

        /// <summary>
        /// Called only on <see cref="ClientEntityManager.Update"/> and if entity has attribute <see cref="UpdateableEntity"/>
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
        
        protected void CreateRPCAction(Action methodToCall, out Action cachedAction)
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            if(!EntityManager.ClassDataDict[ClassId].RemoteCalls.TryGetValue(methodToCall.Method, out var rpcInfo))
                throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");

            if (EntityManager.IsServer)
            {
                if ((rpcInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = () => { methodToCall(); ServerManager.AddRemoteCall(Id, rpcInfo); };
                else
                    cachedAction = () => ServerManager.AddRemoteCall(Id, rpcInfo);
            }
            else
            {
                cachedAction = () =>
                {
                    if (IsLocalControlled && (rpcInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall();
                };
            }
        }
        
        protected void CreateRPCAction<T>(Action<T> methodToCall, out Action<T> cachedAction) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            if(!EntityManager.ClassDataDict[ClassId].RemoteCalls.TryGetValue(methodToCall.Method, out var rpcInfo))
                throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");

            if (EntityManager.IsServer)
            {
                if ((rpcInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = value => { methodToCall(value); ServerManager.AddRemoteCall(Id, value, rpcInfo); };
                else
                    cachedAction = value => ServerManager.AddRemoteCall(Id, value, rpcInfo);
            }
            else
            {
                cachedAction = value =>
                {
                    if (IsLocalControlled && (rpcInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value);
                };
            }
        }
        
        protected void CreateRPCAction<T>(Action<T[], ushort> methodToCall, out Action<T[], ushort> cachedAction) where T : struct
        {
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            if(!EntityManager.ClassDataDict[ClassId].RemoteCalls.TryGetValue(methodToCall.Method, out var rpcInfo))
                throw new Exception($"{methodToCall.Method.Name} is not [RemoteCall] method");
            
            if (EntityManager.IsServer)
            {
                if ((rpcInfo.Flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = (value, count) => { methodToCall(value, count); ServerManager.AddRemoteCall(Id, value, count, rpcInfo); };
                else
                    cachedAction = (value, count) => ServerManager.AddRemoteCall(Id, value, count, rpcInfo);
            }
            else
            {
                cachedAction = (value, count) =>
                {
                    if (IsLocalControlled && (rpcInfo.Flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value, count);
                };
            }
        }

        int IComparable<InternalEntity>.CompareTo(InternalEntity other)
        {
            return Id - other.Id;
        }
    }
}