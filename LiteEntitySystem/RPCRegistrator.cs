using System;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public delegate void SpanAction<TCaller, T>(TCaller caller, ReadOnlySpan<T> data) where TCaller : InternalSyncType where T : unmanaged;

    [AttributeUsage(AttributeTargets.Field)]
    public class BindRpc : Attribute
    {
        public BindRpc(string methodName)
        {
        }

        public BindRpc(string methodName, ExecuteFlags flags)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall
    {
        internal ushort LocalId;
        internal Action<InternalSyncType> CachedActionServer;
        internal Action<InternalSyncType> CachedActionClient;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCall<T> where T : unmanaged
    {
        internal ushort LocalId;
        internal Action<InternalSyncType, T> CachedActionServer;
        internal Action<InternalSyncType, T> CachedActionClient;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteCallSpan<T> where T : unmanaged
    {
        internal ushort LocalId;
        internal SpanAction<InternalSyncType, T> CachedActionServer;
        internal SpanAction<InternalSyncType, T> CachedActionClient;
    }
    
    public delegate void MethodCallDelegate(InternalSyncType classPtr, ReadOnlySpan<byte> buffer);
}