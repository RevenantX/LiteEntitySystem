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
        public static GeneratedClassMetadata GetMetadata(SyncableField s)
        {
            return s.GetClassMetadata();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnSyncRequested(SyncableField s)
        {
            s.OnSyncRequested();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InternalSyncablesSetup(SyncableField s, InternalEntity parent, ushort syncableId)
        {
            s.ParentEntity = parent;
            s.SyncableId = syncableId;
        }
    }
}