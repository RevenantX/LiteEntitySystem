using System;

namespace LiteEntitySystem.Internal
{
    public struct EntityFieldInfo
    {
        public readonly string Name; //used for debug
        public readonly uint Size;
        public readonly int IntSize;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;
        public readonly Type ActualType;
        
        public int FixedOffset;
        
        public EntityFieldInfo(
            string name,
            Type type,
            int size,
            SyncFlags flags)
        {
            ActualType = type;
            Name = name;
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