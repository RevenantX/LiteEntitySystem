using System;
using System.Collections.Generic;
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

        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass> methodToCall) =>
            (classPtr, _) => methodToCall((TClass)classPtr);
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCall<T> where T : unmanaged
    {
        internal readonly Delegate CachedAction;
        
        internal RemoteCall(Delegate cachedAction)
        {
            CachedAction = cachedAction;
        }
        
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                fixed (byte* data = buffer)
                    methodToCall((TClass)classPtr, *(T*)data);
            };
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RemoteCallSpan<T> where T : unmanaged
    {
        internal readonly Delegate CachedAction;
        
        internal RemoteCallSpan(Delegate cachedAction)
        {
            CachedAction = cachedAction;
        }

        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T> methodToCall) =>
            (classPtr, buffer) => methodToCall((TClass)classPtr, MemoryMarshal.Cast<byte, T>(buffer));
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

    public readonly ref struct RPCRegistrator
    {
        private readonly List<RpcFieldInfo> _calls;

        internal RPCRegistrator(List<RpcFieldInfo> remoteCallsList)
        {
            _calls = remoteCallsList;
        }

        internal static void CheckTarget(object ent, object target)
        {
            if (ent != target)
                throw new Exception("You can call this only on this class methods");
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action (OnSync will be called after RPCs and OnConstructs)
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction) where T : unmanaged where TEntity : InternalEntity
        {
            BindOnChange(self, ref syncVar, onChangedAction, OnSyncExecutionOrder.AfterConstruct);
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        /// <param name="executionOrder">order of execution</param>
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction, OnSyncExecutionOrder executionOrder) where T : unmanaged where TEntity : InternalEntity
        {
            CheckTarget(self, onChangedAction.Target);
            ref var field = ref self.GetClassData().Fields[syncVar.FieldId];
            field.OnSyncExecutionOrder = executionOrder;
            field.OnSync = RemoteCall<T>.CreateMCD(onChangedAction.Method.CreateDelegateHelper<Action<TEntity, T>>());
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
            CreateRPCAction(methodToCall.Method.CreateDelegateHelper<Action<TEntity>>(), ref remoteCallHandle, flags);
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
            CreateRPCAction(methodToCall.Method.CreateDelegateHelper<Action<TEntity, T>>(), ref remoteCallHandle, flags);
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
            CheckTarget(self, methodToCall.Target);
            CreateRPCAction(methodToCall.Method.CreateDelegateHelper<SpanAction<TEntity, T>>(), ref remoteCallHandle, flags);
        }
        
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity>(Action<TEntity> methodToCall, ref RemoteCall remoteCallHandle, ExecuteFlags flags) where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCall.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall((Action<InternalEntity>)(e =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e);
                    e.ServerManager.AddRemoteCall(e.Id, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e);
            }));
        }
        
        /// <summary>
        /// Creates cached rpc action with valueType argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(Action<TEntity, T> methodToCall, ref RemoteCall<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCall<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall<T>((Action<InternalEntity, T>)((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v);
            }));
        }

        /// <summary>
        /// Creates cached rpc action with Span argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(SpanAction<TEntity, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCallSpan<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCallSpan<T>((SpanAction<InternalEntity, T>)((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v);
            }));
        }
    }

    public ref struct SyncableRPCRegistrator
    {
        private readonly List<RpcFieldInfo> _calls;
        private readonly int _syncableOffset;
        private ushort _internalRpcCounter;

        internal SyncableRPCRegistrator(int syncableOffset, List<RpcFieldInfo> remoteCallsList)
        {
            _calls = remoteCallsList;
            _internalRpcCounter = 0;
            _syncableOffset = syncableOffset;
        }
        
        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            CreateClientAction(methodToCall.Method.CreateDelegateHelper<Action<TSyncField>>(), ref remoteCallHandle);
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            CreateClientAction(methodToCall.Method.CreateDelegateHelper<Action<TSyncField, T>>(), ref remoteCallHandle);
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            CreateClientAction(methodToCall.Method.CreateDelegateHelper<SpanAction<TSyncField, T>>(), ref remoteCallHandle);
        }

        public void CreateClientAction<TSyncField>(Action<TSyncField> methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall((Action<SyncableField>)(s => s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, (ushort)(rpcId + s.RPCOffset), s.Flags)));
        }

        public void CreateClientAction<T, TSyncField>(Action<TSyncField, T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall<T>((Action<SyncableField, T>)((s, value) => s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, value, (ushort)(rpcId + s.RPCOffset), s.Flags)));
        }
        
        public void CreateClientAction<T, TSyncField>(SpanAction<TSyncField, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSpan<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCallSpan<T>((SpanAction<SyncableField, T>)((s, value) => s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, value, (ushort)(rpcId + s.RPCOffset), s.Flags)));
        }
    }
}