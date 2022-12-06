namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        SyncVar,
        SyncVarWithNotification,
        SyncableSyncVar
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
            FieldType = FieldType.SyncableSyncVar;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            OnSync = null;
        }
    }
}