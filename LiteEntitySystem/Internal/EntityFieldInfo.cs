using System;

namespace LiteEntitySystem.Internal
{
    public struct EntityFieldInfo
    {
        public readonly string Name; //used for debug
        public readonly ushort Id;
        public readonly ushort SyncableId;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;
        public readonly bool HasChangeNotification;
        public readonly Type ActualType;

        internal readonly ValueTypeProcessor TypeProcessor;
        public int FixedOffset;
        public int PredictedOffset;
        
        public EntityFieldInfo(
            string name,
            Type type,
            ushort id,
            ushort syncableId,
            SyncFlags flags,
            bool hasChangeNotification)
        {
            ActualType = type;
            Name = name;
            TypeProcessor = ValueProcessors.RegisteredProcessors[type];
            Id = id;
            SyncableId = syncableId;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
            HasChangeNotification = hasChangeNotification;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }
    }
}