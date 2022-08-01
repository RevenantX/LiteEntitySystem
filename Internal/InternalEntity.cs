using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public abstract class InternalEntity : IComparable<InternalEntity>
    {
        /// <summary>
        /// Entity class id
        /// </summary>
        public readonly ushort ClassId;
        
        /// <summary>
        /// Entity instance id
        /// </summary>
        public readonly ushort Id;
        
        /// <summary>
        /// Entity manager
        /// </summary>
        public readonly EntityManager EntityManager;
        
        internal readonly byte Version;

        /// <summary>
        /// Is entity is local controlled
        /// </summary>
        public bool IsLocalControlled => IsControlledBy(EntityManager.InternalPlayerId);
        
        /// <summary>
        /// Is entity is controlled by server
        /// </summary>
        public bool IsServerControlled => !IsControlledBy(EntityManager.InternalPlayerId);
        
        /// <summary>
        /// ClientEntityManager that available only on client. Will throw exception if called on server
        /// </summary>
        public ClientEntityManager ClientManager => (ClientEntityManager)EntityManager;
        
        /// <summary>
        /// ServerEntityManager that available only on server. Will throw exception if called on client
        /// </summary>
        public ServerEntityManager ServerManager => (ServerEntityManager)EntityManager;

        internal abstract bool IsControlledBy(byte playerId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref EntityClassData GetClassData()
        {
            return ref EntityManager.ClassDataDict[ClassId];
        }

        /// <summary>
        /// Is locally created entity
        /// </summary>
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        /// <summary>
        /// Called when entity manager is reset
        /// </summary>
        public virtual void Free()
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

        public virtual void OnSyncStart()
        {
            
        }

        public virtual void OnSyncEnd()
        {
            
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Id;
            ClassId = entityParams.ClassId;
            Version = entityParams.Version;
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
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
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
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
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call (must have <see cref="RemoteCall"/> attribute)</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
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