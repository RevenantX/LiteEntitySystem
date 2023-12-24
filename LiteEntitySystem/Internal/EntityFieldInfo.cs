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
        public readonly Type ActualType;
        
        public int FixedOffset;
        
        public EntityFieldInfo(
            string name,
            Type type,
            ushort id,
            ushort syncableId,
            int size,
            SyncFlags flags)
        {
            ActualType = type;
            Name = name;
            Id = id;
            SyncableId = syncableId;
            Size = (uint)size;
            IntSize = size;
            FixedOffset = 0;
            Flags = flags;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }
    }
}