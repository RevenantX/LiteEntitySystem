using System;
using System.Runtime.CompilerServices;

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
        public readonly int SyncableSyncVarOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;
        
        public MethodCallDelegate OnSync;
        public BindOnChangeFlags OnSyncFlags;
        public int FixedOffset;
        public int PredictedOffset;
        
        /// <summary>
        /// Direct field offset which for Entities - is SyncVar<T>, and for SyncableField - SyncableField
        /// </summary>
        public readonly int Offset;

        //for value type
        public EntityFieldInfo(string name, ValueTypeProcessor valueTypeProcessor, int offset, SyncFlags flags) : 
            this(name, valueTypeProcessor, offset, -1, flags, FieldType.SyncVar)
        {

        }

        //For syncable syncvar
        public EntityFieldInfo(string name, ValueTypeProcessor valueTypeProcessor, int offset, int syncableSyncVarOffset, SyncFlags flags) :
            this(name, valueTypeProcessor, offset, syncableSyncVarOffset, flags, FieldType.SyncableSyncVar)
        {

        }
        
        private EntityFieldInfo(
            string name,
            ValueTypeProcessor valueTypeProcessor,
            int offset,
            int syncableSyncVarOffset,
            SyncFlags flags,
            FieldType fieldType)
        {
            OnSyncFlags = 0;
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
            Flags = flags;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InternalBaseClass GetTargetObjectAndOffset(InternalEntity entity, out int offset)
        {
            if (FieldType == FieldType.SyncableSyncVar)
            {
                offset = SyncableSyncVarOffset;
                return RefMagic.GetFieldValue<SyncableField>(entity, Offset);
            }
            offset = Offset;
            return entity;
        }
        
        public InternalBaseClass GetTargetObject(InternalEntity entity) =>
            FieldType == FieldType.SyncableSyncVar
                ? RefMagic.GetFieldValue<SyncableField>(entity, Offset)
                : entity;
    }
}