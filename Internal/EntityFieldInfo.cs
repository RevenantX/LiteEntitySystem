using System;

namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        Value,
        Syncable,
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
        public readonly MethodCallDelegate OnSync;
        public readonly SyncFlags Flags;

        public bool IsPredicted => Flags.HasFlagFast(SyncFlags.RemotePredicted) || !Flags.HasFlagFast(SyncFlags.OnlyForRemote);

        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(
            ValueTypeProcessor valueTypeProcessor,
            MethodCallDelegate onSync,
            int offset,
            int size,
            SyncFlags flags)
        {
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = (uint)size;
            IntSize = size;
            FieldType = FieldType.Value;
            OnSync = onSync;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
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
            int size,
            SyncFlags flags)
        {
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = syncableSyncVarOffset;
            Offset = offset;
            Size = (uint)size;
            IntSize = size;
            FieldType = FieldType.SyncableSyncVar;
            OnSync = null;
            FixedOffset = 0;
            PredictedOffset = 0;
            Flags = flags;
        }
    }
}