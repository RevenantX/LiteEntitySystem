using System;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public ref struct RPCRegistrator
    {
        private readonly EntityManager _entityManager;
        private readonly bool _isRpcBound;
        private byte _rpcId;

        internal RPCRegistrator(EntityManager entityManager, bool isRpcBound)
        {
            _entityManager = entityManager;
            _isRpcBound = isRpcBound;
            _rpcId = 0;
        }
        
        public unsafe void BindOnChange<T, TEntity>(TEntity entity, ref SyncVarWithNotify<T> syncVar, Action<TEntity, T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            ref var classData = ref entity.EntityManager.ClassDataDict[entity.ClassId];
            classData.Fields[syncVar.FieldId].OnSync = (ptr, buffer) =>
            {
                fixed(byte* data = buffer)
                    onChangedAction((TEntity)ptr, *(T*)data);
            };
        }
        
        public void BindOnChange<T, TEntity>(TEntity entity, ref SyncVarWithNotify<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            entity.EntityManager.ClassDataDict[entity.ClassId].Fields[syncVar.FieldId].OnSync = MethodCallGenerator.Generate<TEntity, T>(onChangedAction.Method, false);
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity>(TEntity self, Action methodToCall, out Action cachedAction, ExecuteFlags flags = ExecuteFlags.None) where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");

            ref var classData = ref _entityManager.ClassDataDict[ClassId];
            byte rpcId = _rpcId;
            _rpcId++;

            if (_entityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = () => { methodToCall(); ServerManager.AddRemoteCall(Id, rpcId, flags); };
                else
                    cachedAction = () => ServerManager.AddRemoteCall(Id, rpcId, flags);
            }
            else
            {
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId+1);
                classData.RemoteCallsClient[rpcId] ??= MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method);
                cachedAction = () =>
                {
                    if (self.IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall();
                };
            }
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<T, TEntity>(TEntity self, Action<T> methodToCall, out Action<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            
            ref var classData = ref _entityManager.ClassDataDict[ClassId];
            byte rpcId = _rpcId;
            _rpcId++;

            if (_entityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = value => { methodToCall(value); ServerManager.AddRemoteCall(Id, value, rpcId, flags); };
                else
                    cachedAction = value => ServerManager.AddRemoteCall(Id, value, rpcId, flags);
            }
            else
            {
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId+1);
                classData.RemoteCallsClient[rpcId] ??= MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method, false);
                cachedAction = value =>
                {
                    if (self.IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value);
                };
            }
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<T, TEntity>(TEntity self, RemoteCallSpan<T> methodToCall, out RemoteCallSpan<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            
            ref var classData = ref _entityManager.ClassDataDict[ClassId];
            byte rpcId = _rpcId;
            _rpcId++;

            if (_entityManager.IsServer)
            {
                if ((flags & ExecuteFlags.ExecuteOnServer) != 0)
                    cachedAction = value => { methodToCall(value); ServerManager.AddRemoteCall(Id, value, rpcId, flags); };
                else
                    cachedAction = value => ServerManager.AddRemoteCall(Id, value, rpcId, flags);
            }
            else
            {
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId+1);
                classData.RemoteCallsClient[rpcId] ??= MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method, true);
                cachedAction = value =>
                {
                    if (self.IsLocalControlled && (flags & ExecuteFlags.ExecuteOnPrediction) != 0)
                        methodToCall(value);
                };
            }
        }
    }

    public ref struct SyncableRPCRegistrator
    {
        private readonly EntityManager _entityManager;
        private readonly InternalEntity _entity;
        private readonly bool _isRpcBound;
        private byte _rpcId;

        internal SyncableRPCRegistrator(EntityManager entityManager, InternalEntity entity, bool isRpcBound)
        {
            _entityManager = entityManager;
            _entity = entity;
            _isRpcBound = isRpcBound;
            _rpcId = 0;
        }

        private ref MethodCallDelegate GetSyncableRemoteCall(byte rpcId)
        {
            ref var classData = ref _entityManager.ClassDataDict[_entity.ClassId];
            Utils.ResizeIfFull(ref classData.SyncableRemoteCallsClient, rpcId+1);
            return ref classData.SyncableRemoteCallsClient[rpcId];
        }
        
        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, out Action cachedAction) where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method);
            if (_entityManager.IsServer)
            {
                var serverManager = (ServerEntityManager)_entityManager;
                var localEntity = _entity;
                cachedAction = () => serverManager.AddSyncableCall(localEntity.Id, self.FieldId, rpcId);
            }
            else
            {
                cachedAction = null;
            }
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, out Action<T> cachedAction) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method, false);
            if (_entityManager.IsServer)
            {
                var serverManager = (ServerEntityManager)_entityManager;
                var localEntity = _entity;
                cachedAction = value => serverManager.AddSyncableCall(localEntity.Id, self.FieldId, rpcId, value);
            }
            else
            {
                cachedAction = null;
            }
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, RemoteCallSpan<T> methodToCall, out RemoteCallSpan<T> cachedAction) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method, true);
            if(_entityManager.IsServer)
            {
                var serverManager = (ServerEntityManager)_entityManager;
                var localEntity = _entity;
                cachedAction = value => serverManager.AddSyncableCall(localEntity.Id, self.FieldId, rpcId, value);
            }
            else
            {
                cachedAction = null;
            }
        }
    }
}