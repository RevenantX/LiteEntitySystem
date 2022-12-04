namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        Value,
        Syncable,
        SyncableField
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
        public readonly bool ChangeNotification;
        
        public MethodCallDelegate OnSync;
        public bool IsPredicted => Flags.HasFlagFast(SyncFlags.AlwaysPredict) || !Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers);
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            bool changeNotification,
            SyncFlags flags)
        {
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.Value;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            OnSync = null;
            ChangeNotification = changeNotification;
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
            ChangeNotification = false;
        }
        
        //For syncable syncvar
        public EntityFieldInfo(
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            int syncableSyncVarOffset,
            bool changeNotification,
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
            ChangeNotification = changeNotification;
        }
    }
}