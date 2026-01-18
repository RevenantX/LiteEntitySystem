using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [Flags]
    public enum BindOnChangeFlags
    {
        /// <summary>
        /// Execute in OnSync stage. Mostly useful for RemoteControlled entities to trigger change notification
        /// </summary>
        ExecuteOnSync =         1 << 0,
        
        /// <summary>
        /// Execute on local prediction on Client and on rollback
        /// </summary>
        ExecuteOnPrediction =   1 << 1,
        
        /// <summary>
        /// Execute when value changed on server
        /// </summary>
        ExecuteOnServer =       1 << 2,
        
        /// <summary>
        /// Execute when SyncVar values reset in rollback (before OnRollback() called)
        /// </summary>
        ExecuteOnRollbackReset = 1 << 3,
        
        /// <summary>
        /// Execute after entity new() called and initial state read before OnConstructed
        /// </summary>
        ExecuteOnNew =         1 << 4,
        
        /// <summary>
        /// Combines ExecuteOnSync, ExecuteOnPrediction and ExecuteOnServer flags
        /// </summary>
        ExecuteAlways =         ExecuteOnSync | ExecuteOnPrediction | ExecuteOnServer | ExecuteOnRollbackReset
    }
    
    public delegate void SpanAction<T>(ReadOnlySpan<T> data);
    public delegate void SpanAction<in TCaller, T>(TCaller caller, ReadOnlySpan<T> data);
    internal delegate void MethodCallDelegate(InternalBaseClass classPtr, ReadOnlySpan<byte> buffer);
    
    public readonly struct RemoteCall
    {
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass> methodToCall) where TClass : InternalBaseClass =>
            (classPtr, _) => methodToCall((TClass)classPtr);
        
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
        internal static unsafe MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) where TClass : InternalBaseClass =>
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
        internal static MethodCallDelegate CreateMCD<TClass>(SpanAction<TClass, T> methodToCall) where TClass : InternalBaseClass =>
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
    
    public readonly struct RemoteCallSerializable<T> where T : struct, ISpanSerializable
    {
        internal static MethodCallDelegate CreateMCD<TClass>(Action<TClass, T> methodToCall) where TClass : InternalBaseClass =>
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
        public void BindOnChange<TEntity, T>(ref SyncVar<T> syncVar, Action<TEntity, T> onChangedAction, BindOnChangeFlags flags = BindOnChangeFlags.ExecuteOnSync) where T : unmanaged where TEntity : InternalEntity
        {
            _fields[syncVar.FieldId].OnSync = RemoteCall<T>.CreateMCD(onChangedAction);
            _fields[syncVar.FieldId].OnSyncFlags = flags;
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<TEntity, T>(TEntity self, ref SyncVar<T> syncVar, Action<T> onChangedAction, BindOnChangeFlags flags = BindOnChangeFlags.ExecuteOnSync) where T : unmanaged where TEntity : InternalEntity
        {
            CheckTarget(self, onChangedAction.Target);
            BindOnChange(ref syncVar, onChangedAction.Method.CreateDelegateHelper<Action<TEntity, T>>(), flags);
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
        /// This method can be used for virtual methods
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
        /// This method can be used for virtual methods
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
        /// This method can be used for virtual methods
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
        /// This method can be used for virtual methods
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
        private readonly EntityFieldInfo[] _fields;
        private readonly List<RpcFieldInfo> _calls;
        private readonly int _syncableOffset;
        private readonly ushort _initialCallsSize;

        internal SyncableRPCRegistrator(int syncableOffset, List<RpcFieldInfo> remoteCallsList, EntityFieldInfo[] fields)
        {
            _fields = fields;
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
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<TSyncField, T>(ref SyncVar<T> syncVar, Action<TSyncField, T> onChangedAction, BindOnChangeFlags flags = BindOnChangeFlags.ExecuteOnSync) where T : unmanaged where TSyncField : SyncableField
        {
            _fields[syncVar.FieldId].OnSync = RemoteCall<T>.CreateMCD(onChangedAction);
            _fields[syncVar.FieldId].OnSyncFlags = flags;
        }
        
        /// <summary>
        /// Bind notification of SyncVar changes to action
        /// </summary>
        /// <param name="self">Target entity for binding</param>
        /// <param name="syncVar">Variable to bind</param>
        /// <param name="onChangedAction">Action that will be called when variable changes by sync</param>
        public void BindOnChange<TSyncField, T>(TSyncField self, ref SyncVar<T> syncVar, Action<T> onChangedAction, BindOnChangeFlags flags = BindOnChangeFlags.ExecuteOnSync) where T : unmanaged where TSyncField : SyncableField
        {
            RPCRegistrator.CheckTarget(self, onChangedAction.Target);
            BindOnChange(ref syncVar, onChangedAction.Method.CreateDelegateHelper<Action<TSyncField, T>>(), flags);
        }
    }
}