using System;

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
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(
            string name,
            Type type,
            ushort id,
            SyncFlags flags,
            bool hasChangeNotification)
        {
            Name = name;
            TypeProcessor = ValueProcessors.RegisteredProcessors[type];
            SyncableId = 0;
            Id = id;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.SyncVar;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            HasChangeNotification = hasChangeNotification;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }

        //For syncable syncvar
        public EntityFieldInfo(
            string name,
            Type type,
            ushort syncableId,
            SyncFlags flags)
        {
            HasChangeNotification = false;
            Name = name;
            TypeProcessor = ValueProcessors.RegisteredProcessors[type];
            SyncableId = syncableId;
            Id = 0;
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