using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<T>(ReadOnlySpan<T> data) where T : unmanaged;
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data) where T : unmanaged;

    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall
    {
        internal ushort RpcId;
        internal Action<InternalSyncType> CachedActionServer;
        internal Action<InternalSyncType> CachedActionClient;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall<T> where T : unmanaged
    {
        internal ushort RpcId;
        internal Action<InternalSyncType, T> CachedActionServer;
        internal Action<InternalSyncType, T> CachedActionClient;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCallSpan<T> where T : unmanaged
    {
        internal ushort RpcId;
        internal SpanAction<InternalSyncType, T> CachedActionServer;
        internal SpanAction<InternalSyncType, T> CachedActionClient;
    }
    
    internal delegate void MethodCallDelegate(object classPtr, ReadOnlySpan<byte> buffer);

    public readonly ref struct RPCRegistrator
    {
        /// <summary>
        /// Creates cached rpc action
        /// </summary>
        /// <param name="self">Target entity with RPC</param>
        /// <param name="methodToCall">RPC method to call</param>
        /// <param name="remoteCallHandle">output handle that should be used to call rpc</param>
        /// <param name="flags">RPC execution flags</param>
        public void CreateRPCAction<TEntity>(TEntity self, Action methodToCall, ref RemoteCall remoteCallHandle, ExecuteFlags flags) where TEntity : InternalEntity
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var d = methodToCall.Method.CreateSelfDelegate<TEntity>();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    remoteCallHandle.CachedActionServer = e =>
                    {
                        var te = (TEntity)e;
                        d(te);
                        te.ServerManager.AddRemoteCall(te.Id, rpcId, flags);
                    };
                else
                    remoteCallHandle.CachedActionServer = e =>
                    {
                        var te = (TEntity)e;
                        te.ServerManager.AddRemoteCall(te.Id, rpcId, flags);
                    };
            }
            else
            {
                self.GetClassMetadata().RpcData[rpcId].ClientMethod = MethodCallGenerator.GenerateNoParams<TEntity>(methodToCall.Method);
                if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                    remoteCallHandle.CachedActionClient = e =>
                    {
                        var te = (TEntity)e;
                        if (te.IsLocalControlled)
                            d(te);
                    };
                else
                    remoteCallHandle.CachedActionClient = _ => { };
            }
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
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var d = methodToCall.Method.CreateSelfDelegate<TEntity, T>();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    remoteCallHandle.CachedActionServer = (e, value) =>
                    {
                        var te = (TEntity)e;
                        d(te, value); 
                        te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                    };
                else
                    remoteCallHandle.CachedActionServer = (e, value) =>
                    {
                        var te = (TEntity)e;
                        te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                    };
            }
            else
            {
                self.GetClassMetadata().RpcData[rpcId].ClientMethod = MethodCallGenerator.Generate<TEntity, T>(methodToCall.Method);
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                    remoteCallHandle.CachedActionClient = (e, value) =>
                    {
                        var te = (TEntity)e;
                        if (te.IsLocalControlled) 
                            d(te, value);
                    };
                else
                    remoteCallHandle.CachedActionClient = (_, _) => { };
            }
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
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var d = methodToCall.Method.CreateSelfDelegateSpan<TEntity, T>();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.EntityManager.IsServer)
            {
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                    remoteCallHandle.CachedActionServer = (e, value) =>
                    {
                        var te = (TEntity)e;
                        d(te, value); 
                        te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                    };
                else
                    remoteCallHandle.CachedActionServer = (e, value) =>
                    {
                        var te = (TEntity)e;
                        te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                    };
            }
            else
            {
                self.GetClassMetadata().RpcData[rpcId].ClientMethod = MethodCallGenerator.GenerateSpan<TEntity, T>(methodToCall.Method);
                if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                    remoteCallHandle.CachedActionClient = (e, value) =>
                    {
                        var te = (TEntity)e;
                        if (te.IsLocalControlled) 
                            d(te, value);
                    };
                else
                    remoteCallHandle.CachedActionClient = (_, _) => { };
            }
        }
    }

    public readonly ref struct SyncableRPCRegistrator
    {
        public void CreateClientAction<TSyncField>(TSyncField self, Action methodToCall, ref RemoteCall remoteCallHandle) where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var classData = self.ParentEntity.GetClassMetadata();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.ParentEntity.EntityManager.IsServer)
            {
                remoteCallHandle.CachedActionServer = s =>
                {
                    var sf = (SyncableField)s;
                    sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, (ushort)(sf.RpcOffset + rpcId), sf.Flags);
                };
            }
            else
            {
                classData.RpcData[self.RpcOffset + rpcId].ClientMethod = MethodCallGenerator.GenerateNoParams<TSyncField>(methodToCall.Method);
            }
        }

        public void CreateClientAction<T, TSyncField>(TSyncField self, Action<T> methodToCall, ref RemoteCall<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var classData = self.ParentEntity.GetClassMetadata();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.ParentEntity.EntityManager.IsServer)
            {
                remoteCallHandle.CachedActionServer = (s, value) =>
                {
                    var sf = (SyncableField)s;
                    sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(sf.RpcOffset + rpcId), sf.Flags);
                };
            }
            else
            {
                classData.RpcData[self.RpcOffset + rpcId].ClientMethod = MethodCallGenerator.Generate<TSyncField, T>(methodToCall.Method);
            }
        }
        
        public void CreateClientAction<T, TSyncField>(TSyncField self, SpanAction<T> methodToCall, ref RemoteCallSpan<T> remoteCallHandle) where T : unmanaged where TSyncField : SyncableField
        {
            if (methodToCall.Target != self)
                throw new Exception("You can call this only on this class methods");
            var classData = self.ParentEntity.GetClassMetadata();
            ushort rpcId = remoteCallHandle.RpcId;
            if (self.ParentEntity.EntityManager.IsServer)
            {
                remoteCallHandle.CachedActionServer = (s, value) =>
                {
                    var sf = (SyncableField)s;
                    sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(sf.RpcOffset + rpcId), sf.Flags);
                };
            }
            else
            {
                classData.RpcData[self.RpcOffset + rpcId].ClientMethod = MethodCallGenerator.GenerateSpan<TSyncField, T>(methodToCall.Method);
            }
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