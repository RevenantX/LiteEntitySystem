using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data);
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data);
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);
    internal delegate void OnSyncCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);
    
    public readonly struct RemoteCall
    {
        internal readonly Action<InternalBaseClass> CachedAction;
        internal RemoteCall(Action<InternalBaseClass> action) => CachedAction = action;
        internal void Call(InternalBaseClass self) => CachedAction?.Invoke(self);
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass> methodToCall) => (classPtr, _) => methodToCall((TClass)classPtr);
    }
    
    public readonly struct RemoteCall<T> where T : unmanaged
    {
        internal readonly Action<InternalBaseClass, T> CachedAction;
        internal RemoteCall(Action<InternalBaseClass, T> action) => CachedAction = action;
        internal void Call(InternalBaseClass self, T data) => CachedAction?.Invoke(self, data);
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                fixed (byte* data = buffer)
                    methodToCall((TClass)classPtr, *(T*)data);
            };
    }
    
    public readonly struct RemoteCallSpan<T> where T : unmanaged
    {
        internal readonly SpanAction<InternalBaseClass, T> CachedAction;
        internal RemoteCallSpan(SpanAction<InternalBaseClass, T> action) => CachedAction = action;
        internal void Call(InternalBaseClass self, ReadOnlySpan<T> data) => CachedAction?.Invoke(self, data);
        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T> methodToCall) =>
            (classPtr, buffer) => methodToCall((TClass)classPtr, MemoryMarshal.Cast<byte, T>(buffer));
    }
    
    public readonly struct RemoteCallSerializable<T> where T : struct, ISpanSerializable
    {
        internal readonly Action<InternalBaseClass, T> CachedAction;
        internal RemoteCallSerializable(Action<InternalBaseClass, T> action) => CachedAction = action;
        internal void Call(InternalBaseClass self, T data) => CachedAction?.Invoke(self, data);
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                var t = default(T);
                t.Deserialize(new SpanReader(buffer));
                methodToCall((TClass)classPtr, t);
            };
    }

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
            BindOnChange(self, ref syncVar, onChangedAction, self.ClassData.Fields[syncVar.FieldId].OnSyncExecutionOrder);
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
            //if (self.EntityManager.IsServer)
            //    return;
            CheckTarget(self, onChangedAction.Target);
            ref var field = ref self.ClassData.Fields[syncVar.FieldId];
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
        /// Creates cached rpc action with ISpanSerializable argument
        /// </summary>
        /// <param name="self">Target entity with RPC</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(TEntity self, Action<T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle, ExecuteFlags flags) 
            where T : struct, ISpanSerializable 
            where TEntity : InternalEntity
        {
            CheckTarget(self, methodToCall.Target);
            CreateRPCAction(methodToCall.Method.CreateDelegateHelper<Action<TEntity, T>>(), ref remoteCallHandle, flags);
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
            remoteCallHandle = new RemoteCall(e =>
            {
                var te = (TEntity)e;
                if (te.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall(te);
                    te.ServerManager.AddRemoteCall(te.Id, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && te.IsLocalControlled)
                    methodToCall(te);
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
            remoteCallHandle = new RemoteCall<T>((e,v) =>
            {
                var te = (TEntity)e;
                if (te.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall(te, v);
                    te.ServerManager.AddRemoteCall(te.Id, new ReadOnlySpan<T>(&v, 1), rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && te.IsLocalControlled)
                    methodToCall(te, v);
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
            remoteCallHandle = new RemoteCallSpan<T>((e,v) =>
            {
                var te = (TEntity)e;
                if (te.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall(te, v);
                    te.ServerManager.AddRemoteCall(te.Id, v, rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && te.IsLocalControlled)
                    methodToCall(te, v);
            });
        }
        
        /// <summary>
        /// Creates cached rpc action with ISpanSerializable argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public unsafe void CreateRPCAction<TEntity, T>(Action<TEntity, T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle, ExecuteFlags flags) 
            where T : struct, ISpanSerializable
            where TEntity : InternalEntity
        {
            ushort rpcId = (ushort)_calls.Count;
            _calls.Add(new RpcFieldInfo(RemoteCallSerializable<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCallSerializable<T>((e,v) =>
            {
                var te = (TEntity)e;
                if (te.IsServer)
                {
                    if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                        methodToCall(te, v);
                    var writer = new SpanWriter(stackalloc byte[v.MaxSize]);
                    v.Serialize(writer);
                    te.ServerManager.AddRemoteCall<byte>(te.Id, writer.RawData.Slice(0, writer.Position), rpcId, flags);
                }
                else if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction) && te.IsLocalControlled)
                    methodToCall(te, v);
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
        
        public void CreateClientAction<TSyncField, T>(TSyncField self, Action<T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle) where T : struct, ISpanSerializable where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, methodToCall.Target);
            CreateClientAction(methodToCall.Method.CreateDelegateHelper<Action<TSyncField, T>>(), ref remoteCallHandle);
        }

        public void CreateClientAction<TSyncField>(Action<TSyncField> methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall(s =>
            {
                var sf = (SyncableField)s;
                if(sf.IsServer)
                    sf.ParentEntityInternal?.ServerManager.AddRemoteCall(sf.ParentEntityInternal.Id, (ushort)(rpcId + sf.RPCOffset), sf.Flags);
            });
        }

        public unsafe void CreateClientAction<TSyncField, T>(Action<TSyncField, T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCall<T>((s, value) =>
            {
                var sf = (SyncableField)s;
                if(sf.IsServer)
                    sf.ParentEntityInternal?.ServerManager.AddRemoteCall(sf.ParentEntityInternal.Id, new ReadOnlySpan<T>(&value, 1), (ushort)(rpcId + sf.RPCOffset), sf.Flags);
            });
        }
        
        public void CreateClientAction<TSyncField, T>(SpanAction<TSyncField, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSpan<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCallSpan<T>((s, value) =>
            {
                var sf = (SyncableField)s;
                if(sf.IsServer)
                    sf.ParentEntityInternal?.ServerManager.AddRemoteCall(sf.ParentEntityInternal.Id, value, (ushort)(rpcId + sf.RPCOffset), sf.Flags);
            });
        }
        
        public unsafe void CreateClientAction<TSyncField, T>(Action<TSyncField, T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle) where T : struct, ISpanSerializable where TSyncField : SyncableField
        {
            ushort rpcId = _internalRpcCounter++;
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSerializable<T>.CreateMCD(methodToCall)));
            if (remoteCallHandle.CachedAction != null)
                return;
            remoteCallHandle = new RemoteCallSerializable<T>((s, value) =>
            {
                var sf = (SyncableField)s;
                if (sf.IsServer)
                {
                    var writer = new SpanWriter(stackalloc byte[value.MaxSize]);
                    value.Serialize(writer);
                    sf.ParentEntityInternal?.ServerManager.AddRemoteCall<byte>(sf.ParentEntityInternal.Id, writer.RawData.Slice(0, writer.Position), (ushort)(rpcId + sf.RPCOffset), sf.Flags);
                }
            });
        }
    }
}