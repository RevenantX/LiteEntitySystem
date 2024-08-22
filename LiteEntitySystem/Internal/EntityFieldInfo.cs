namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        SyncVar,
        SyncableSyncVar
    }

    public enum OnSyncExecutionOrder
    {
        AfterConstruct,
        BeforeConstruct
    }

    internal struct EntityFieldInfo
    {
        public readonly string Name; //used for debug
        public readonly ValueTypeProcessor TypeProcessor;
        public readonly int Offset;
        public readonly int SyncableSyncVarOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;
        public OnSyncExecutionOrder OnSyncExecutionOrder;
        
        public OnSyncCallDelegate OnSync;
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            SyncVarFlags flags)
        {
            Name = name;
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.SyncVar;
            FixedOffset = 0;
            PredictedOffset = 0;
            OnSync = null;
            Flags = flags?.Flags ?? SyncFlags.None;
            OnSyncExecutionOrder = flags?.OnSyncExecutionOrder ?? OnSyncExecutionOrder.AfterConstruct;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }

        //For syncable syncvar
        public EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            int syncableSyncVarOffset,
            SyncVarFlags flags)
        {
            Name = name;
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = syncableSyncVarOffset;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = FieldType.SyncableSyncVar;
            FixedOffset = 0;
            PredictedOffset = 0;
            OnSync = null;
            Flags = flags?.Flags ?? SyncFlags.None;
            OnSyncExecutionOrder = flags?.OnSyncExecutionOrder ?? OnSyncExecutionOrder.AfterConstruct;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }
    }
}