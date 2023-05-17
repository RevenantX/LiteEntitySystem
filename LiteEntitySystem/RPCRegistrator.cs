using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data) where T : unmanaged;
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data) where T : unmanaged;

    public enum RPCType
    {
        NoParams,
        OneValue,
        Array
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall
    {
        internal readonly ushort RpcId;
        internal Delegate CachedAction;

        internal RemoteCall(ushort rpcId, Delegate cachedAction)
        {
            RpcId = rpcId;
            CachedAction = cachedAction;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall<T> where T : unmanaged
    {
        internal readonly ushort RpcId;
        internal Delegate CachedAction;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCallSpan<T> where T : unmanaged
    {
        internal readonly ushort RpcId;
        internal Delegate CachedAction;
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

    public readonly ref struct RPCRegistrator
    {
        /// <summary>
        /// Bind notification of SyncVarWithNotify changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVarWithNotify<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            if (onChangedAction.Target != self)
                throw new Exception("You can call this only on this class methods");
            self.GetClassData().Fields[syncVar.FieldId].OnSync = MethodCallGenerator.Generate<TEntity, T>(onChangedAction.Method);
        }

        private static Delegate Create<TEntity, T>(
            TEntity self,
            ushort rpcId,
            Delegate methodToCall,
            ExecuteFlags flags,
            RPCType type) where TEntity : InternalEntity where T : unmanaged
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            ref var classData = ref self.GetClassData();
            Delegate cachedAction;
            if (type == RPCType.NoParams)
            {
                var d = methodToCall.Method.CreateSelfDelegate<TEntity>();
                Action<InternalEntity> t;
                if (self.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        t = e => { d((TEntity)e); e.ServerManager.AddRemoteCall(e.Id, rpcId, flags); };
                    else
                        t = e => e.ServerManager.AddRemoteCall(e.Id, rpcId, flags);
                }
                else
                {
                    classData.RemoteCallsClient[rpcId] = MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method);
                    if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        t = e => { if (e.IsLocalControlled) d((TEntity)e); };
                    else
                        t = _ => { };
                }
                cachedAction = t;
            }
            else if(type == RPCType.OneValue)
            {
                var d = methodToCall.Method.CreateSelfDelegate<TEntity, T>();
                Action<InternalEntity, T> t;
                if (self.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        t = (e, value) => { d((TEntity)e, value); e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags); };
                    else
                        t = (e, value) => e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags);
                }
                else
                {
                    classData.RemoteCallsClient[rpcId] = MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method);
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        t = (e, value) => { if (e.IsLocalControlled) d((TEntity)e, value); };
                    else
                        t = (_, _) => { };
                }
                cachedAction = t;
            }
            else //Array
            {
                var d = methodToCall.Method.CreateSelfDelegateSpan<TEntity, T>();
                SpanAction<InternalEntity, T> t;
                if (self.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        t = (e, value) => { d((TEntity)e, value); e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags); };
                    else
                        t = (e, value) => e.ServerManager.AddRemoteCall(e.Id, value, rpcId, flags);
                }
                else
                {
                    classData.RemoteCallsClient[rpcId] = MethodCallGenerator.GenerateSpan<TEntity, T>(methodToCall.Method);
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                        t = (e, value) => { if (e.IsLocalControlled) d((TEntity)e, value); };
                    else
                        t = (_, _) => { };
                }
                cachedAction = t;
            }
            classData.RemoteCallsServer[rpcId] = cachedAction;
            return cachedAction;
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="self">Target entity with RPC</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity>(TEntity self, Action methodToCall, ref RemoteCall remoteCallHandle, ExecuteFlags flags) where TEntity : InternalEntity
        {
            remoteCallHandle.CachedAction = Create<TEntity, int>(self, remoteCallHandle.RpcId, methodToCall, flags, RPCType.NoParams);
        }

        /// <summary>
        /// Creates cached rpc action with valueType argument
        /// </summary>
        /// <param name="self">Target entity with RPC</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            remoteCallHandle.CachedAction = Create<TEntity, T>(self, remoteCallHandle.RpcId, methodToCall, flags, RPCType.OneValue);
        }

        /// <summary>
        /// Creates cached rpc action with Span argument
        /// </summary>
        /// <param name="self">Target entity with RPC</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            remoteCallHandle.CachedAction = Create<TEntity, T>(self, remoteCallHandle.RpcId, methodToCall, flags, RPCType.Array);
        }
    }

    public readonly ref struct SyncableRPCRegistrator
    {
        private readonly InternalEntity _entity;

        internal SyncableRPCRegistrator(InternalEntity entity)
        {
            _entity = entity;
        }

        private Delegate CreateAction<T, TSyncField>(TSyncField self, Delegate methodToCall, ushort rpcId, RPCType type)
            where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            ref var classData = ref _entity.GetClassData();
            Delegate cachedAction = null;
            if (_entity.EntityManager.IsServer)
            {
                var serverManager = _entity.ServerManager;
                cachedAction = type switch
                {
                    RPCType.NoParams => (Action<SyncableField>) (s => serverManager.AddRemoteCall(s.ParentEntityId, rpcId, s.Flags)),
                    RPCType.OneValue => (Action<SyncableField, T>) ((s, value) => serverManager.AddRemoteCall(s.ParentEntityId, value, rpcId, s.Flags)),
                    RPCType.Array => (SpanAction<SyncableField, T>) ((s, value) => serverManager.AddRemoteCall(s.ParentEntityId, value, rpcId, s.Flags)),
                    _ => null
                };
                classData.RemoteCallsServer[rpcId] = cachedAction;
            }
            else
            {
                classData.RemoteCallsClient[rpcId] = type switch
                {
                    RPCType.NoParams => MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method),
                    RPCType.OneValue => MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method),
                    RPCType.Array => MethodCallGenerator.GenerateSpan<TSyncField, T>(methodToCall.Method),
                    _ => null
                };
            }
            return cachedAction;
        }

        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            remoteCallHandle.CachedAction = CreateAction<int, TSyncField>(self, methodToCall, remoteCallHandle.RpcId, RPCType.NoParams);
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            remoteCallHandle.CachedAction = CreateAction<T, TSyncField>(self, methodToCall, remoteCallHandle.RpcId, RPCType.OneValue);
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            remoteCallHandle.CachedAction = CreateAction<T, TSyncField>(self, methodToCall, remoteCallHandle.RpcId, RPCType.Array);
        }
    }

    internal static class MethodCallGenerator
    {
        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d = method.CreateDelegateHelper<Action<TClass,TValue>>();
            return (classPtr, buffer) =>
            {
                fixed(byte* data = buffer)
                    d((TClass)classPtr, *(TValue*)data);
            };
        }
        
        public static MethodCallDelegate GenerateSpan<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d =  method.CreateDelegateHelper<SpanAction<TClass, TValue>>();
            return (classPtr, buffer) => d((TClass)classPtr, MemoryMarshal.Cast<byte, TValue>(buffer));
        }

        public static MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method) 
        {
            var d =  method.CreateDelegateHelper<Action<TClass>>();
            return (classPtr, _) => d((TClass)classPtr);
        }
    }
}