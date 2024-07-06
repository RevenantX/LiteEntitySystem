using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data) where T : unmanaged;
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data) where T : unmanaged;
    public delegate void SpanAction<TCaller, T1, T2>(TCaller caller, ReadOnlySpan<T1> data1, ReadOnlySpan<T2> data2) where T1 : unmanaged where T2 : unmanaged;
    public delegate void ValueSpanAction<TCaller, T1, T2>(TCaller caller, T1 data1, ReadOnlySpan<T2> data2) where T1 : unmanaged where T2 : unmanaged;
    
    public struct RemoteCall
    {
        internal Delegate CachedAction;
        internal static RemoteCall Create<TClass>(Action<TClass> action) => new() { CachedAction = action };
        internal void Call<TClass>(TClass self) => ((Action<TClass>)CachedAction)?.Invoke(self);
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass> methodToCall) => (classPtr, _, _) => methodToCall((TClass)classPtr);
    }
    
    public struct RemoteCall<T> where T : unmanaged
    {
        internal Delegate CachedAction;
        internal static RemoteCall<T> Create<TClass>(Action<TClass, T> action) => new() { CachedAction = action };
        internal void Call<TClass>(TClass self, T data) => ((Action<TClass, T>)CachedAction)?.Invoke(self, data);
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer, _) =>
            {
                fixed (byte* data = buffer)
                    methodToCall((TClass)classPtr, *(T*)data);
            };
    }
    
    public struct RemoteCallSpan<T> where T : unmanaged
    {
        internal Delegate CachedAction;
        internal static RemoteCallSpan<T> Create<TClass>(SpanAction<TClass, T> action) => new() { CachedAction = action };
        internal void Call<TClass>(TClass self, ReadOnlySpan<T> data) => ((SpanAction<TClass, T>)CachedAction)?.Invoke(self, data);
        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T> methodToCall) =>
            (classPtr, buffer, _) => methodToCall((TClass)classPtr, MemoryMarshal.Cast<byte, T>(buffer));
    }
    
    public struct RemoteCallValueSpan<T1, T2> where T1 : unmanaged where T2 : unmanaged
    {
        internal Delegate CachedAction;
        internal static RemoteCallValueSpan<T1, T2> Create<TClass>(ValueSpanAction<TClass, T1, T2> action) => new() { CachedAction = action };
        internal void Call<TClass>(TClass self, T1 data1, ReadOnlySpan<T2> data2) => ((ValueSpanAction<TClass, T1, T2>)CachedAction)?.Invoke(self, data1, data2);
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(ValueSpanAction<TClass, T1, T2> methodToCall) =>
            (classPtr, buffer1, buffer2) =>
            {
                fixed (byte* data1 = buffer1)
                    methodToCall((TClass)classPtr, *(T1*)data1, MemoryMarshal.Cast<byte, T2>(buffer2));
            };
    }
    
    public struct RemoteCallSpan<T1, T2> where T1 : unmanaged where T2 : unmanaged
    {
        internal Delegate CachedAction;
        internal static RemoteCallSpan<T1, T2> Create<TClass>(SpanAction<TClass, T1, T2> action) => new() { CachedAction = action };
        internal void Call<TClass>(TClass self, ReadOnlySpan<T1> data1, ReadOnlySpan<T2> data2) => ((SpanAction<TClass, T1, T2>)CachedAction)?.Invoke(self, data1, data2);
        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T1, T2> methodToCall) =>
            (classPtr, buffer1, buffer2) => methodToCall((TClass)classPtr, MemoryMarshal.Cast<byte, T1>(buffer1), MemoryMarshal.Cast<byte, T2>(buffer2));
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer1, ReadOnlySpan<byte> buffer2);
    internal delegate void OnSyncdCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

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
        public unsafe void BindOnChange<T, TEntity>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction, OnSyncExecutionOrder executionOrder) where T : unmanaged where TEntity : InternalEntity
        {
            CheckTarget(self, onChangedAction.Target);
            ref var field = ref self.GetClassData().Fields[syncVar.FieldId];
            field.OnSyncExecutionOrder = executionOrder;
            var methodToCall = onChangedAction.Method.CreateDelegateHelper<Action<TEntity, T>>();
            field.OnSync = (classPtr, buffer) =>
            {
                fixed (byte* data = buffer)
                    methodToCall((TEntity)classPtr, *(T*)data);
            };
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
        /// Creates cached rpc action with valueType and Span argument
        /// </summary>
        /// <param name="self">Target entity with RPC just for auto type deducting</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T1, T2>(TEntity self, ValueSpanAction<TEntity, T1, T2> methodToCall, ref RemoteCallValueSpan<T1, T2> remoteCallHandle, ExecuteFlags flags) 
            where T1 : unmanaged
            where T2 : unmanaged
            where TEntity : InternalEntity =>
            CreateRPCAction(methodToCall, ref remoteCallHandle, flags);

        /// <summary>
        /// Creates cached rpc action with Span argument
        /// </summary>
        /// <param name="self">Target entity with RPC just for auto type deducting</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T1, T2>(TEntity self, SpanAction<TEntity, T1, T2> methodToCall, ref RemoteCallSpan<T1, T2> remoteCallHandle, ExecuteFlags flags) 
            where T1 : unmanaged
            where T2 : unmanaged
            where TEntity : InternalEntity =>
            CreateRPCAction(methodToCall, ref remoteCallHandle, flags);
        
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
            remoteCallHandle = RemoteCall.Create<InternalEntity>(e =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e);
                    e.ServerManager.AddRemoteCall(e.Id, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e);
            });
        }
        
        /// <summary>
        /// Creates cached rpc action with valueType argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public unsafe void CreateRPCAction<TEntity, T>(Action<TEntity, T> methodToCall, ref RemoteCall<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCall<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCall<T>.Create<InternalEntity>((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, new ReadOnlySpan<T>(&v, 1), rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v);
            });
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
            remoteCallHandle = RemoteCallSpan<T>.Create<InternalEntity>((e,v) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v);
                    e.ServerManager.AddRemoteCall(e.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v);
            });
        }
        
        /// <summary>
        /// Creates cached rpc action with valueType and Span argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public unsafe void CreateRPCAction<TEntity, T1, T2>(ValueSpanAction<TEntity, T1, T2> methodToCall, ref RemoteCallValueSpan<T1, T2> remoteCallHandle, ExecuteFlags flags) 
            where T1 : unmanaged
            where T2 : unmanaged
            where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCallValueSpan<T1, T2>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCallValueSpan<T1, T2>.Create<InternalEntity>((e,v1,v2) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v1, v2);
                    e.ServerManager.AddRemoteCall(e.Id, new ReadOnlySpan<T1>(&v1, 1), v2, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v1, v2);
            });
        }

        /// <summary>
        /// Creates cached rpc action with Span argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T1, T2>(SpanAction<TEntity, T1, T2> methodToCall, ref RemoteCallSpan<T1, T2> remoteCallHandle, ExecuteFlags flags) 
            where T1 : unmanaged
            where T2 : unmanaged
            where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCallSpan<T1, T2>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCallSpan<T1, T2>.Create<InternalEntity>((e,v1,v2) =>
            {
                if (e.EntityManager.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall((TEntity)e, v1, v2);
                    e.ServerManager.AddRemoteCall(e.Id, v1, v2, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && e.IsLocalControlled)
                    methodToCall((TEntity)e, v1, v2);
            });
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

        public void CreateClientAction<TSyncField, T>(TSyncField self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            CreateClientAction(methodToCall.Method.CreateDelegateHelper<Action<TSyncField, T>>(), ref remoteCallHandle);
        }
        
        public void CreateClientAction<TSyncField, T>(TSyncField self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
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
            remoteCallHandle = RemoteCall.Create<SyncableField>(s => s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, (ushort)(rpcId + s.RPCOffset), s.Flags));
        }

        public unsafe void CreateClientAction<TSyncField, T>(Action<TSyncField, T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCall<T>.Create<SyncableField>((s, value) =>
            {
                s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, new ReadOnlySpan<T>(&value, 1), (ushort)(rpcId + s.RPCOffset), s.Flags);
            });
        }
        
        public void CreateClientAction<TSyncField, T>(SpanAction<TSyncField, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSpan<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCallSpan<T>.Create<SyncableField>((s, value) =>
            {
                s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, value, (ushort)(rpcId + s.RPCOffset), s.Flags);
            });
        }
        
        public unsafe void CreateClientAction<TSyncField, T1, T2>(ValueSpanAction<TSyncField, T1, T2> methodToCall, ref RemoteCallValueSpan<T1, T2> remoteCallHandle)
            where T1 : unmanaged
            where T2 : unmanaged
            where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallValueSpan<T1, T2>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCallValueSpan<T1, T2>.Create<SyncableField>((s, value1, value2) =>
            {
                s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, new ReadOnlySpan<T1>(&value1, 1), value2, (ushort)(rpcId + s.RPCOffset), s.Flags);
            });
        }
        
        public void CreateClientAction<TSyncField, T1, T2>(SpanAction<TSyncField, T1, T2> methodToCall, ref RemoteCallSpan<T1, T2> remoteCallHandle)
            where T1 : unmanaged
            where T2 : unmanaged
            where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSpan<T1, T2>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = RemoteCallSpan<T1, T2>.Create<SyncableField>((s, value1, value2) =>
            {
                s.ServerEntityManager?.AddRemoteCall(s.ParentEntityInternal.Id, value1, value2, (ushort)(rpcId + s.RPCOffset), s.Flags);
            });
        }
    }
}