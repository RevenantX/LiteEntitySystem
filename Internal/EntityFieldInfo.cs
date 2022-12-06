namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        SyncVar,
        SyncVarWithNotification,
        SyncEntityReference,
        Syncable,
        SyncableField
    }

    internal static class FieldTypeExt
    {
        public static bool HasNotification(this FieldType ft)
        {
            return ft == FieldType.SyncEntityReference || ft == FieldType.SyncVarWithNotification;
        }
    }
    
    internal struct EntityFieldInfo
    {
        public readonly ValueTypeProcessor TypeProcessor;
        public readonly int Offset;
        public readonly int SyncableSyncVarOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;

        public MethodCallDelegate OnSync;
        public bool IsPredicted => Flags.HasFlagFast(SyncFlags.AlwaysPredict) || !Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers);
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            FieldType fieldType,
            SyncFlags flags)
        {
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = fieldType;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            OnSync = null;
        }

        //For syncable
        public EntityFieldInfo(
            int offset,
            SyncFlags flags)
        {
            TypeProcessor = null;
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = 0;
            IntSize = 0;
            FieldType = FieldType.Syncable;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            OnSync = null;
        }
        
        //For syncable syncvar
        public EntityFieldInfo(
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            int syncableSyncVarOffset,
            SyncFlags flags)
        {
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = syncableSyncVarOffset;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.SyncableField;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            OnSync = null;
        }
    }
}