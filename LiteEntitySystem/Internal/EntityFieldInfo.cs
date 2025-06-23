using System.Linq;

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
        public readonly int[] Offsets;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;

        public MethodCallDelegate OnSync;
        public int FixedOffset;
        public int PredictedOffset;

        public EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            int[] offsets,
            SyncVarFlags flags,
            FieldType fieldType)
        {
            Name = name;
            TypeProcessor = valueTypeProcessor;
            Offsets = offsets;
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
            byte* predictedData,
            byte* nextInterpDataPtr,
            byte* prevInterpDataPtr)
        {
            if (IsPredicted)
                RefMagic.CopyBlock(predictedData + PredictedOffset, rawData, Size);
                
            if (Flags.HasFlagFast(SyncFlags.Interpolated))
            {
                if (nextInterpDataPtr != null)
                    RefMagic.CopyBlock(nextInterpDataPtr + FixedOffset, rawData, Size);
                if (prevInterpDataPtr != null)
                    RefMagic.CopyBlock(prevInterpDataPtr + FixedOffset, rawData, Size);
            }

            if (OnSync != null)
            {
                if (TypeProcessor.SetFromAndSync(entity, Offsets, rawData))
                    return true; //create sync call
            }
            else
            {
                TypeProcessor.SetFrom(entity, Offsets, rawData);
            }

            return false;
        }
    }
}