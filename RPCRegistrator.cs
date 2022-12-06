using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void RemoteCall();
    public delegate void RemoteCall<T>(T data) where T : unmanaged;
    public delegate void RemoteCallSpan<T>(ReadOnlySpan<T> data) where T : unmanaged;
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);
    
    public ref struct RPCRegistrator
    {
        private readonly bool _isRpcBound;
        private byte _rpcId;

        internal RPCRegistrator(bool isRpcBound)
        {
            _isRpcBound = isRpcBound;
            _rpcId = 0;
        }

        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVarWithNotify<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            if (onChangedAction.Target != self)
                throw new Exception("You can call this only on this class methods");
            self.GetClassData().Fields[syncVar.FieldId].OnSync = MethodCallGenerator.Generate<TEntity, T>(onChangedAction.Method);
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity>(TEntity self, Action methodToCall, out RemoteCall cachedAction, ExecuteFlags flags = ExecuteFlags.None) where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
            {
                ref var classData = ref self.GetClassData();
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId + 1);
                classData.RemoteCallsClient[rpcId] ??=
                    MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method);
            }

            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = () => { methodToCall(); self.ServerManager.AddRemoteCall(self.Id, rpcId, flags); };
                else
                    cachedAction = () => self.ServerManager.AddRemoteCall(self.Id, rpcId, flags);
            }
            else
            {
                cachedAction = () =>
                {
                    if (self.IsLocalControlled && flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        methodToCall();
                };
            }
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, Action<T> methodToCall, out RemoteCall<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
            {
                ref var classData = ref self.GetClassData();
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId);
                classData.RemoteCallsClient[rpcId] ??= MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method);
            }

            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = value => { methodToCall(value); self.ServerManager.AddRemoteCall(self.Id, value, rpcId, flags); };
                else
                    cachedAction = value => self.ServerManager.AddRemoteCall(self.Id, value, rpcId, flags);
            }
            else
            {
                cachedAction = value =>
                {
                    if (self.IsLocalControlled && flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        methodToCall(value);
                };
            }
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, RemoteCallSpan<T> methodToCall, out RemoteCallSpan<T> cachedAction, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
            {
                ref var classData = ref self.GetClassData();
                Utils.ResizeIfFull(ref classData.RemoteCallsClient, rpcId);
                classData.RemoteCallsClient[rpcId] ??= MethodCallGenerator.GenerateSpan<TEntity, T>(methodToCall.Method);
            }

            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = value => { methodToCall(value); self.ServerManager.AddRemoteCall(self.Id, value, rpcId, flags); };
                else
                    cachedAction = value => self.ServerManager.AddRemoteCall(self.Id, value, rpcId, flags);
            }
            else
            {
                cachedAction = value =>
                {
                    if (self.IsLocalControlled && flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        methodToCall(value);
                };
            }
        }
    }

    public ref struct SyncableRPCRegistrator
    {
        private readonly InternalEntity _entity;
        private readonly bool _isRpcBound;
        private byte _rpcId;

        internal SyncableRPCRegistrator(InternalEntity entity, bool isRpcBound)
        {
            _entity = entity;
            _isRpcBound = isRpcBound;
            _rpcId = 0;
        }

        private ref MethodCallDelegate GetSyncableRemoteCall(byte rpcId)
        {
            ref var classData = ref _entity.GetClassData();
            Utils.ResizeIfFull(ref classData.SyncableRemoteCallsClient, rpcId+1);
            return ref classData.SyncableRemoteCallsClient[rpcId];
        }
        
        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, out RemoteCall cachedAction) where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method);
            if (_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                var localEntity = _entity;
                cachedAction = () => serverManager.AddSyncableCall(localEntity.Id, self.FieldId, rpcId);
            }
            else
            {
                cachedAction = null;
            }
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, out RemoteCall<T> cachedAction) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            if (!_isRpcBound)
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method);
            if (_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
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
                GetSyncableRemoteCall(rpcId) = MethodCallGenerator.GenerateSpan<TSyncField, T>(methodToCall.Method);
            if(_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                var localEntity = _entity;
                cachedAction = value => serverManager.AddSyncableCall(localEntity.Id, self.FieldId, rpcId, value);
            }
            else
            {
                cachedAction = null;
            }
        }
    }

    internal static class MethodCallGenerator
    {
        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d = (Action<TClass, TValue>)method.CreateDelegate(typeof(Action<TClass, TValue>));
            return (classPtr, buffer) =>
            {
                fixed(byte* data = buffer)
                    d((TClass)classPtr, *(TValue*)data);
            };
        }
        
        public static MethodCallDelegate GenerateSpan<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d = (ArrayBinding<TClass, TValue>)method.CreateDelegate(typeof(ArrayBinding<TClass, TValue>));
            return (classPtr, buffer) => d((TClass)classPtr, MemoryMarshal.Cast<byte, TValue>(buffer));
        }

        public static MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method) 
        {
            var d = (Action<TClass>)method.CreateDelegate(typeof(Action<TClass>));
            return (classPtr, _) => d((TClass)classPtr);
        }
    }
}