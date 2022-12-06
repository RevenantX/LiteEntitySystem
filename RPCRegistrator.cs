using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data) where T : unmanaged;
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data) where T : unmanaged;

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCall
    {
        internal readonly byte RpcId;
        internal readonly object CachedAction;

        internal RemoteCall(byte rpcId, object cachedAction)
        {
            RpcId = rpcId;
            CachedAction = cachedAction;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCall<T> where T : unmanaged
    {
        internal readonly byte RpcId;
        internal readonly object CachedAction;

        internal RemoteCall(byte rpcId, object cachedAction)
        {
            RpcId = rpcId;
            CachedAction = cachedAction;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCallSpan<T> where T : unmanaged
    {
        internal readonly byte RpcId;
        internal readonly object CachedAction;

        internal RemoteCallSpan(byte rpcId, object cachedAction)
        {
            RpcId = rpcId;
            CachedAction = cachedAction;
        }
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

    public ref struct RPCRegistrator
    {
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVarWithNotify<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            if (onChangedAction.Target != self)
                throw new Exception("You can call this only on this class methods");
            self.GetClassData().Fields[syncVar.FieldId].OnSync = MethodCallGenerator.Generate<TEntity, T>(onChangedAction.Method);
        }
        
        private void CreateRpcDelegate(InternalEntity entity, object methodTarget, MethodCallDelegate d, byte rpcId)
        {
            if (methodTarget != entity)
                throw new Exception("You can call this only on this class methods");
            ref var classData = ref entity.GetClassData();
            classData.RemoteCallsClient[rpcId] = d;
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity>(TEntity self, Action methodToCall, ref RemoteCall remoteCallHandle, ExecuteFlags flags = ExecuteFlags.None) where TEntity : InternalEntity
        {
            byte rpcId = remoteCallHandle.RpcId;
            CreateRpcDelegate(self, methodToCall.Target, MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method), rpcId);
            var d = (Action<TEntity>)methodToCall.Method.CreateDelegate(typeof(Action<TEntity>));
            Action<InternalEntity> cachedAction;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = e => { d((TEntity)e); e.ServerManager.AddRemoteCall(e.Id, rpcId, flags); };
                else
                    cachedAction = e => e.ServerManager.AddRemoteCall(e.Id, rpcId, flags);
            }
            else if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
            {
                cachedAction = e => { if (e.IsLocalControlled) d((TEntity)e); };
            }
            else
            {
                cachedAction = _ => { };
            }
            self.GetClassData().RPCCache[rpcId] = cachedAction;
            remoteCallHandle = new RemoteCall(rpcId, cachedAction);
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            byte rpcId = remoteCallHandle.RpcId;
            CreateRpcDelegate(self, methodToCall.Target, MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method), rpcId);
            var d = (Action<TEntity, T>)methodToCall.Method.CreateDelegate(typeof(Action<TEntity, T>));
            Action<InternalEntity, T> cachedAction;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = (e, value) => { d((TEntity)e, value); e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags); };
                else
                    cachedAction = (e, value) => e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags);
            }
            else if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
            {
                cachedAction = (e, value) => { if (e.IsLocalControlled) d((TEntity)e, value); };
            }
            else
            {
                cachedAction = (_, _) => { };
            }
            self.GetClassData().RPCCache[rpcId] = cachedAction;
            remoteCallHandle = new RemoteCall<T>(rpcId, cachedAction);
        }

        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="cachedAction">output action that should be used to call rpc</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle, ExecuteFlags flags = ExecuteFlags.None) where T : unmanaged where TEntity : InternalEntity
        {
            byte rpcId = remoteCallHandle.RpcId;
            CreateRpcDelegate(self, methodToCall.Target, MethodCallGenerator.GenerateSpan<TEntity, T>(methodToCall.Method), rpcId);
            var d = (SpanAction<TEntity, T>)methodToCall.Method.CreateDelegate(typeof(SpanAction<TEntity, T>));
            SpanAction<InternalEntity, T> cachedAction;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    cachedAction = (e, value) => { d((TEntity)e, value); e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags); };
                else
                    cachedAction = (e, value) => e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags);
            }
            else if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
            {
                cachedAction = (e, value) => { if (e.IsLocalControlled) d((TEntity)e, value); };
            }
            else
            {
                cachedAction = (_, _) => { };
            }
            self.GetClassData().RPCCache[rpcId] = cachedAction;
            remoteCallHandle = new RemoteCallSpan<T>(rpcId, cachedAction);
        }
    }

    public ref struct SyncableRPCRegistrator
    {
        private readonly InternalEntity _entity;
        private byte _rpcId;

        internal SyncableRPCRegistrator(InternalEntity entity)
        {
            _entity = entity;
            _rpcId = 0;
        }

        private ref MethodCallDelegate GetSyncableRemoteCall(byte rpcId)
        {
            ref var classData = ref _entity.GetClassData();
            return ref classData.SyncableRemoteCallsClient[rpcId];
        }
        
        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, out RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            GetSyncableRemoteCall(rpcId) = MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method);
            Action<SyncableField> cachedAction = null;
            if (_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                var localEntity = _entity;
                cachedAction = s => serverManager.AddSyncableCall(localEntity.Id, s.FieldId, rpcId);
            }
            remoteCallHandle = new RemoteCall(0, cachedAction);
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, out RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            GetSyncableRemoteCall(rpcId) = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method);
            Action<SyncableField, T> cachedAction = null;
            if (_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                var localEntity = _entity;
                cachedAction = (s, value) => serverManager.AddSyncableCall(localEntity.Id, s.FieldId, rpcId, value);
            }
            remoteCallHandle = new RemoteCall<T>(0, cachedAction);
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, SpanAction<T> methodToCall, out RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            byte rpcId = _rpcId;
            _rpcId++;
            GetSyncableRemoteCall(rpcId) = MethodCallGenerator.GenerateSpan<TSyncField, T>(methodToCall.Method);
            SpanAction<SyncableField, T> cachedAction = null;
            if(_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                var localEntity = _entity;
                cachedAction = (s, value) => serverManager.AddSyncableCall(localEntity.Id, s.FieldId, rpcId, value);
            }
            remoteCallHandle = new RemoteCallSpan<T>(0, cachedAction);
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