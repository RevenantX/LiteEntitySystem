using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data);
    public delegate void SpanAction<in TCaller, T>(TCaller caller, ReadOnlySpan<T> data);
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);
    
    public readonly struct RemoteCall
    {
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass> methodToCall) => (classPtr, _) => methodToCall((TClass)classPtr);
        
        internal readonly Action<InternalEntity> CachedAction;
        internal readonly ushort Id;
        internal readonly ExecuteFlags Flags;
        internal readonly bool Initialized;
        
        internal RemoteCall(Action<InternalEntity> action, ushort rpcId, ExecuteFlags flags)
        {
            CachedAction = action;
            Id = rpcId;
            Flags = flags;
            Initialized = true;
        }
    }
    
    public readonly struct RemoteCall<T> where T : unmanaged
    {
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                fixed (byte* data = buffer)
                    methodToCall((TClass)classPtr, *(T*)data);
            };
        
        internal readonly Action<InternalEntity, T> CachedAction;
        internal readonly ushort Id;
        internal readonly ExecuteFlags Flags;
        internal readonly bool Initialized;
        
        internal RemoteCall(Action<InternalEntity, T> action, ushort rpcId, ExecuteFlags flags)
        {
            CachedAction = action;
            Id = rpcId;
            Flags = flags;
            Initialized = true;
        }
    }
    
    public readonly struct RemoteCallSpan<T> where T : unmanaged
    {
        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T> methodToCall) =>
            (classPtr, buffer) => methodToCall((TClass)classPtr, MemoryMarshal.Cast<byte, T>(buffer));
        
        internal readonly SpanAction<InternalEntity, T> CachedAction;
        internal readonly ushort Id;
        internal readonly ExecuteFlags Flags;
        internal readonly bool Initialized;

        internal RemoteCallSpan(SpanAction<InternalEntity, T> action, ushort rpcId, ExecuteFlags flags)
        {
            CachedAction = action;
            Id = rpcId;
            Flags = flags;
            Initialized = true;
        }
    }
    
    public readonly struct RemoteCallNetSerializable<T> where T : INetSerializable, new()
    {
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                var t = new T();
                var dataReader = new NetDataReader(buffer.ToArray());
                t.Deserialize(dataReader);
                methodToCall((TClass)classPtr, t);
            };

        internal readonly Action<InternalEntity, T> CachedAction;
        internal readonly ushort Id;
        internal readonly ExecuteFlags Flags;
        internal readonly bool Initialized;

        internal RemoteCallNetSerializable(Action<InternalEntity, T> action, ushort rpcId, ExecuteFlags flags)
        {
            CachedAction = action;
            Id = rpcId;
            Flags = flags;
            Initialized = true;
        }
    }
    
    public readonly struct RemoteCallSerializable<T> where T : struct, ISpanSerializable
    {
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) =>
            (classPtr, buffer) =>
            {
                var t = default(T);
                var spanReader = new SpanReader(buffer);
                t.Deserialize(ref spanReader);
                methodToCall((TClass)classPtr, t);
            };
        
        internal readonly Action<InternalEntity, T> CachedAction;
        internal readonly ushort Id;
        internal readonly ExecuteFlags Flags;
        internal readonly bool Initialized;

        internal RemoteCallSerializable(Action<InternalEntity, T> action, ushort rpcId, ExecuteFlags flags)
        {
            CachedAction = action;
            Id = rpcId;
            Flags = flags;
            Initialized = true;
        }
    }

    public readonly ref struct RPCRegistrator
    {
        private readonly List<RpcFieldInfo> _calls;
        private readonly EntityFieldInfo[] _fields;

        internal RPCRegistrator(List<RpcFieldInfo> remoteCallsList, EntityFieldInfo[] fields)
        {
            _calls = remoteCallsList;
            _fields = fields;
        }

        internal static void CheckTarget(object ent, object target)
        {
            if (ent != target)
                throw new Exception("You can call this only on this class methods");
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        /// <param name="executionOrder">order of execution</param>
        public void BindOnChange<T, TEntity>(ref SyncVar<T> syncVar, Action<TEntity, T> onChangedAction, OnSyncExecutionOrder executionOrder = OnSyncExecutionOrder.AfterConstruct) where T : unmanaged where TEntity : InternalEntity
        {
            ref var field = ref _fields[syncVar.FieldId];
            field.OnSyncExecutionOrder = executionOrder;
            field.OnSync = RemoteCall<T>.CreateMCD(onChangedAction);
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        /// <param name="executionOrder">order of execution</param>
        public void BindOnChange<T, TEntity>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction, OnSyncExecutionOrder executionOrder = OnSyncExecutionOrder.AfterConstruct) where T : unmanaged where TEntity : InternalEntity
        {
            CheckTarget(self, onChangedAction.Target);
            BindOnChange(ref syncVar, onChangedAction.Method.CreateDelegateHelper<Action<TEntity, T>>(), executionOrder);
        }
        
        
        public void CreateRPCAction<TEntity, T>(
            TEntity self,
            Action<T> methodToCall,
            ref RemoteCallNetSerializable<T> remoteCallHandle,
            ExecuteFlags flags
        ) 
            where T : INetSerializable, new()
            where TEntity : InternalEntity
        {
            CheckTarget(self, methodToCall.Target);

            if (!remoteCallHandle.Initialized)
            {
                var finalFlags = flags | ExecuteFlags.ExecuteOnServer;
                remoteCallHandle = new RemoteCallNetSerializable<T>(
                    (e, val) => methodToCall.Method.CreateDelegateHelper<Action<TEntity, T>>()((TEntity)e, val),
                    (ushort)_calls.Count,
                    finalFlags
                );
            }

            _calls.Add(new RpcFieldInfo(RemoteCallNetSerializable<T>.CreateMCD(
                methodToCall.Method.CreateDelegateHelper<Action<TEntity, T>>()
            )));
        }

        public void CreateRPCAction<TEntity, T>(
            Action<TEntity, T> methodToCall,
            ref RemoteCallNetSerializable<T> remoteCallHandle,
            ExecuteFlags flags
        ) 
            where T : INetSerializable, new()
            where TEntity : InternalEntity
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCallNetSerializable<T>(
                    (e, v) => methodToCall((TEntity)e, v),
                    (ushort)_calls.Count,
                    flags
                );
            _calls.Add(new RpcFieldInfo(RemoteCallNetSerializable<T>.CreateMCD(methodToCall)));
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
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCall(e => methodToCall((TEntity)e), (ushort)_calls.Count, flags);
            _calls.Add(new RpcFieldInfo(RemoteCall.CreateMCD(methodToCall)));
        }
        
        /// <summary>
        /// Creates cached rpc action with valueType argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(Action<TEntity, T> methodToCall, ref RemoteCall<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCall<T>((e, v) => methodToCall((TEntity)e, v), (ushort)_calls.Count, flags);
            _calls.Add(new RpcFieldInfo(RemoteCall<T>.CreateMCD(methodToCall)));
        }

        /// <summary>
        /// Creates cached rpc action with Span argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(SpanAction<TEntity, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle, ExecuteFlags flags) where T : unmanaged where TEntity : InternalEntity
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCallSpan<T>((e,v) => methodToCall((TEntity)e, v), (ushort)_calls.Count, flags);
            _calls.Add(new RpcFieldInfo(RemoteCallSpan<T>.CreateMCD(methodToCall)));
        }
        
        /// <summary>
        /// Creates cached rpc action with ISpanSerializable argument
        /// </summary>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity, T>(Action<TEntity, T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle, ExecuteFlags flags) 
            where T : struct, ISpanSerializable
            where TEntity : InternalEntity
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCallSerializable<T>((e,v) => methodToCall((TEntity)e, v), (ushort)_calls.Count, flags);
            _calls.Add(new RpcFieldInfo(RemoteCallSerializable<T>.CreateMCD(methodToCall)));
        }
    }

    public readonly ref struct SyncableRPCRegistrator
    {
        private readonly List<RpcFieldInfo> _calls;
        private readonly int _syncableOffset;
        private readonly ushort _initialCallsSize;

        internal SyncableRPCRegistrator(int syncableOffset, List<RpcFieldInfo> remoteCallsList)
        {
            _calls = remoteCallsList;
            _initialCallsSize = (ushort)_calls.Count;
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
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCall(null, (ushort)(_calls.Count - _initialCallsSize), 0);
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall.CreateMCD(methodToCall)));
        }

        public void CreateClientAction<TSyncField, T>(Action<TSyncField, T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCall<T>(null, (ushort)(_calls.Count - _initialCallsSize), 0);
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCall<T>.CreateMCD(methodToCall)));
        }
        
        public void CreateClientAction<TSyncField, T>(SpanAction<TSyncField, T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCallSpan<T>(null, (ushort)(_calls.Count - _initialCallsSize), 0);
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSpan<T>.CreateMCD(methodToCall)));
        }
        
        public void CreateClientAction<TSyncField, T>(Action<TSyncField, T> methodToCall, ref RemoteCallSerializable<T> remoteCallHandle) where T : struct, ISpanSerializable where TSyncField : SyncableField
        {
            if (!remoteCallHandle.Initialized)
                remoteCallHandle = new RemoteCallSerializable<T>(null, (ushort)(_calls.Count - _initialCallsSize), 0);
            _calls.Add(new RpcFieldInfo(_syncableOffset, RemoteCallSerializable<T>.CreateMCD(methodToCall)));
        }
    }
}