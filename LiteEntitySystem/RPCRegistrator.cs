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
        internal readonly Delegate CachedAction;

        internal RemoteCall(Delegate cachedAction)
        {
            CachedAction = cachedAction;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCall<T> where T : unmanaged
    {
        internal readonly Delegate CachedAction;
        
        internal RemoteCall(Delegate cachedAction)
        {
            CachedAction = cachedAction;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCallSpan<T> where T : unmanaged
    {
        internal readonly Delegate CachedAction;
        
        internal RemoteCallSpan(Delegate cachedAction)
        {
            CachedAction = cachedAction;
        }
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

    public ref struct RPCRegistrator
    {
        private ushort _rpcId; 
        
        internal RPCRegistrator(ushort rpcId)
        {
            _rpcId = rpcId;
        }

        internal static void CheckTarget(object ent, object target)
        {
            if (ent != target)
                throw new Exception("You can call this only on this class methods");
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            CheckTarget(self, onChangedAction.Target);
            self.GetClassData().Fields[syncVar.FieldId].OnSync = MethodCallGenerator.Generate<TEntity, T>(onChangedAction.Method);
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
            CheckTarget(self, methodToCall.Target);
            var d = methodToCall.Method.CreateDelegateHelper<Action<TEntity>>();
            ushort rpcId = _rpcId++;
            self.GetClassData().RemoteCallsClient[rpcId].Method = MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method);
            remoteCallHandle = new RemoteCall((Action<InternalEntity>)(e =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        d((TEntity)e);
                    e.ServerManager.AddRemoteCall(e.Id, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    d((TEntity)e);
            }));
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
            CheckTarget(self, methodToCall.Target);
            var d = methodToCall.Method.CreateDelegateHelper<Action<TEntity, T>>();
            ushort rpcId = _rpcId++;
            self.GetClassData().RemoteCallsClient[rpcId].Method = MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method);
            remoteCallHandle = new RemoteCall<T>((Action<InternalEntity, T>)((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        d((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    d((TEntity)e, v);
            }));
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
            var d = methodToCall.Method.CreateDelegateHelper<SpanAction<TEntity, T>>();
            ushort rpcId = _rpcId++;
            self.GetClassData().RemoteCallsClient[rpcId].Method = MethodCallGenerator.GenerateSpan<TEntity, T>(methodToCall.Method);
            remoteCallHandle = new RemoteCallSpan<T>((SpanAction<InternalEntity, T>)((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        d((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    d((TEntity)e, v);
            }));
        }
    }

    public ref struct SyncableRPCRegistrator
    {
        private readonly RpcFieldInfo[] _remoteCallsClient;
        internal ushort RpcId;

        internal SyncableRPCRegistrator(RpcFieldInfo[] remoteCallsClient)
        {
            _remoteCallsClient = remoteCallsClient;
            RpcId = 0;
        }

        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            ushort rpcId = RpcId++;
            _remoteCallsClient[rpcId].Method = MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method);
            remoteCallHandle = new RemoteCall((Action<SyncableField>)(s => (s.ParentEntity.EntityManager as ServerEntityManager)?.AddRemoteCall(s.ParentEntity.Id, rpcId, s.Flags)));
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            ushort rpcId = RpcId++;
            _remoteCallsClient[rpcId].Method = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method);
            remoteCallHandle = new RemoteCall<T>((Action<SyncableField, T>)((s, value) => (s.ParentEntity.EntityManager as ServerEntityManager)?.AddRemoteCall(s.ParentEntity.Id, value, rpcId, s.Flags)));
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            ushort rpcId = RpcId++;
            _remoteCallsClient[rpcId].Method = MethodCallGenerator.GenerateSpan<TSyncField, T>(methodToCall.Method);
            remoteCallHandle = new RemoteCallSpan<T>((SpanAction<SyncableField, T>)((s, value) => (s.ParentEntity.EntityManager as ServerEntityManager)?.AddRemoteCall(s.ParentEntity.Id, value, rpcId, s.Flags)));
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