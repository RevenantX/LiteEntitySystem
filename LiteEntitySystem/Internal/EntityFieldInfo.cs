namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        SyncVar,
        SyncableSyncVar
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
        
        public MethodCallDelegate OnSync;
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(string name, ValueTypeProcessor valueTypeProcessor, int offset, SyncVarFlags flags) : 
            this(name, valueTypeProcessor, offset, -1, flags, FieldType.SyncVar)
        {

        }

        //For syncable syncvar
        public EntityFieldInfo(string name, ValueTypeProcessor valueTypeProcessor, int offset, int syncableSyncVarOffset, SyncVarFlags flags) :
            this(name, valueTypeProcessor, offset, syncableSyncVarOffset, flags, FieldType.SyncableSyncVar)
        {

        }
        
        private EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            int syncableSyncVarOffset,
            SyncVarFlags flags,
            FieldType fieldType)
        {
            Name = name;
            TypeProcessor = valueTypeProcessor;
            SyncableSyncVarOffset = syncableSyncVarOffset;
            Offset = offset;
            Size = (uint)TypeProcessor.Size;
            IntSize = TypeProcessor.Size;
            FieldType = fieldType;
            FixedOffset = 0;
            PredictedOffset = 0;
            OnSync = null;
            Flags = flags?.Flags ?? SyncFlags.None;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }


        public unsafe bool ReadField(
            InternalEntity entity, 
            byte* rawData, 
            int readerPosition, 
            byte* predictedData, 
            byte* nextInterpDataPtr, 
            byte* prevInterpDataPtr)
        {
            rawData += readerPosition;
            if (IsPredicted)
                RefMagic.CopyBlock(predictedData + PredictedOffset, rawData, Size);
            if (FieldType == FieldType.SyncableSyncVar)
            {
                var syncableField = RefMagic.RefFieldValue<SyncableField>(entity, Offset);
                TypeProcessor.SetFrom(syncableField, SyncableSyncVarOffset, rawData);
            }
            else
            {
                if (Flags.HasFlagFast(SyncFlags.Interpolated))
                {
                    if(nextInterpDataPtr != null)
                        RefMagic.CopyBlock(nextInterpDataPtr + FixedOffset, rawData, Size);
                    if(prevInterpDataPtr != null)
                        RefMagic.CopyBlock(prevInterpDataPtr + FixedOffset, rawData, Size);
                }

                if (OnSync != null)
                {
                    if (TypeProcessor.SetFromAndSync(entity, Offset, rawData))
                        return true; //create sync call
                }
                else
                {
                    TypeProcessor.SetFrom(entity, Offset, rawData);
                }
            }

            return false;
        }
    }
}