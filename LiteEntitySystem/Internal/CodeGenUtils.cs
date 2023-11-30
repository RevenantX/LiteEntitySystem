using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public static class CodeGenUtils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FieldManipulator GetFieldManipulator(SyncableField s)
        {
            return s.GetFieldManipulator();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnSyncRequested(SyncableField s)
        {
            s.OnSyncRequested();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterRPC(SyncableField s)
        {
            s.RegisterRPC(new SyncableRPCRegistrator());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InternalSyncablesSetup(SyncableField s, InternalEntity parent, ushort rpcOffset)
        {
            s.ParentEntity = parent;
            s.RpcOffset = rpcOffset;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InitRPCData(InternalEntity entity, ushort size)
        {
            entity.GetClassMetadata().RpcData = new RpcData[size];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CheckInitialized(InternalEntity entity)
        {
            return entity.GetClassMetadata().IsRpcBound;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkInitialized(InternalEntity entity)
        {
            entity.GetClassMetadata().IsRpcBound = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRemoteCallId(ref RemoteCall rc, ushort rpcId)
        {
            rc.RpcId = rpcId;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRemoteCallId<T>(ref RemoteCall<T> rc, ushort rpcId) where T : unmanaged
        {
            rc.RpcId = rpcId;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRemoteCallId<T>(ref RemoteCallSpan<T> rc, ushort rpcId) where T : unmanaged
        {
            rc.RpcId = rpcId;
        }
    }
}