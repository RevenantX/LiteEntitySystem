using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        Value,
        Entity,
        Syncable,
        SyncableSyncVar
    }
    
    internal struct EntityFieldInfo
    {
        public readonly int Offset;
        public readonly int SyncableSyncVarOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly UIntPtr PtrSize;
        public readonly FieldType FieldType;
        public readonly MethodCallDelegate OnSync;
        public readonly InterpolatorDelegate Interpolator;
        public readonly SyncFlags Flags;

        public int FixedOffset;

        //for value type
        public EntityFieldInfo(
            MethodCallDelegate onSync, 
            InterpolatorDelegate interpolator,
            int offset,
            int size,
            SyncFlags flags,
            bool isEntityReference)
        {
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = (uint)size;
            IntSize = size;
            PtrSize = (UIntPtr)Size;
            FieldType = isEntityReference ? FieldType.Entity : FieldType.Value;
            OnSync = onSync;
            Interpolator = interpolator;
            FixedOffset = 0;
            Flags = flags;
        }

        //For syncable
        public EntityFieldInfo(
            int offset,
            SyncFlags flags)
        {
            SyncableSyncVarOffset = -1;
            Offset = offset;
            Size = 0;
            IntSize = 0;
            PtrSize = (UIntPtr)Size;
            FieldType = FieldType.Syncable;
            Interpolator = null;
            FixedOffset = 0;
            Flags = flags;
            OnSync = null;
        }
        
        //For syncable syncvar
        public EntityFieldInfo(
            int offset,
            int syncableSyncVarOffset,
            int size,
            SyncFlags flags)
        {
            SyncableSyncVarOffset = syncableSyncVarOffset;
            Offset = offset;
            Size = (uint)size;
            IntSize = size;
            PtrSize = (UIntPtr)Size;
            FieldType = FieldType.SyncableSyncVar;
            OnSync = null;
            Interpolator = null;
            FixedOffset = 0;
            Flags = flags;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void GetToFixedOffset(byte* entityPointer, byte* outData)
        {
            Unsafe.CopyBlock(outData + FixedOffset, entityPointer + Offset, Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetFromFixedOffset(byte* entityPointer, byte* data)
        {
            Unsafe.CopyBlock(entityPointer + Offset, data + FixedOffset, Size);
        }
    }
}