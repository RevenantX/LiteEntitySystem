namespace LiteEntitySystem.Internal
{
    public enum FieldType
    {
        SyncVar,
        SyncableSyncVar
    }

    public struct EntityFieldInfo
    {
        public readonly string Name; //used for debug
        public readonly ushort Id;
        public readonly ushort SyncableId;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;
        public readonly bool HasChangeNotification;

        internal readonly ValueTypeProcessor TypeProcessor;
        internal int FixedOffset;
        internal int PredictedOffset;

        //for value type
        internal EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            ushort id,
            FieldType fieldType,
            SyncFlags flags,
            bool hasChangeNotification)
        {
            Name = name;
            TypeProcessor = valueTypeProcessor;
            SyncableId = 0;
            Id = id;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = fieldType;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            HasChangeNotification = hasChangeNotification;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }

        //For syncable syncvar
        internal EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            ushort id,
            ushort syncableId,
            SyncFlags flags)
        {
            HasChangeNotification = false;
            Name = name;
            TypeProcessor = valueTypeProcessor;
            SyncableId = syncableId;
            Id = id;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.SyncableSyncVar;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }
    }
}