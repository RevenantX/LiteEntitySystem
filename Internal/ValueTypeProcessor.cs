using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    internal static class ValueProcessors
    {
        public static readonly Dictionary<Type, ValueTypeProcessor> RegisteredProcessors = new ();
    }
    
    internal abstract unsafe class ValueTypeProcessor
    {
        internal abstract Type ValueType { get; }
        internal abstract int Size { get; }
        internal abstract bool CompareAndWrite(object obj, int offset, byte* data);
        internal abstract void SetFrom(object obj, int offset, byte* data);
        internal abstract bool SetFromAndSync(object obj, int offset, byte* data, byte* tempData);
        internal abstract void WriteTo(object obj, int offset, byte* data);
        internal abstract void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer);
        internal abstract void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
    }

    internal abstract unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        internal override Type ValueType => typeof(T);
        internal override int Size => sizeof(T);

        internal override void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            throw new Exception($"This type: {typeof(T)} can't be interpolated");
        }
        
        internal override void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.RefFieldValue<T>(obj, offset);
            *(T*)tempHistory = a;
            a = *(T*)historyA;
        }
        
        internal override void SetFrom(object obj, int offset, byte* data)
        {
            ref var a = ref Utils.RefFieldValue<T>(obj, offset);
            a = *(T*)data;
        }

        internal override bool SetFromAndSync(object obj, int offset, byte* data, byte* tempData)
        {
            ref var a = ref Utils.RefFieldValue<T>(obj, offset);
            if (!Compare(ref a, ref *(T*)data))
            {
                *(T*)tempData = a;
                a = *(T*)data;
                *(T*)data = *(T*)tempData;
                return true;
            }
            return false;
        }

        internal override void WriteTo(object obj, int offset, byte* data)
        {
            *(T*)data = Utils.RefFieldValue<T>(obj, offset);
        }
        
        internal override bool CompareAndWrite(object obj, int offset, byte* data)
        {
            ref var a = ref Utils.RefFieldValue<T>(obj, offset);
            if (Compare(ref a, ref *(T*)data))
                return false;
            *(T*)data = a;
            return true;
        }

        protected abstract bool Compare(ref T a, ref T b);
    }

    internal class ValueTypeProcessorByte : ValueTypeProcessor<byte>
    {
        protected override bool Compare(ref byte a, ref byte b) => a == b;
    }
    
    internal class ValueTypeProcessorSByte : ValueTypeProcessor<sbyte>
    {
        protected override bool Compare(ref sbyte a, ref sbyte b) => a == b;
    }

    internal class ValueTypeProcessorShort : ValueTypeProcessor<short>
    {
        protected override bool Compare(ref short a, ref short b) => a == b;
    }

    internal class ValueTypeProcessorUShort : ValueTypeProcessor<ushort>
    {
        protected override bool Compare(ref ushort a, ref ushort b) => a == b;
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        protected override bool Compare(ref int a, ref int b) => a == b;
        
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref Utils.RefFieldValue<int>(obj, offset);
            a = Utils.Lerp(*(int*)prev, *(int*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.RefFieldValue<int>(obj, offset);
            *(int*)tempHistory = a;
            a = Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime);
        }
    }

    internal class ValueTypeProcessorUInt : ValueTypeProcessor<uint>
    {
        protected override bool Compare(ref uint a, ref uint b) => a == b;
    }

    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        protected override bool Compare(ref long a, ref long b) => a == b;
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref Utils.RefFieldValue<long>(obj, offset);
            a = Utils.Lerp(*(long*)prev, *(long*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.RefFieldValue<long>(obj, offset);
            *(long*)tempHistory = a;
            a = Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorULong : ValueTypeProcessor<ulong>
    {
        protected override bool Compare(ref ulong a, ref ulong b) => a == b;
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        protected override bool Compare(ref float a, ref float b) => a == b;
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref Utils.RefFieldValue<float>(obj, offset);
            a = Utils.Lerp(*(float*)prev, *(float*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.RefFieldValue<float>(obj, offset);
            *(float*)tempHistory = a;
            a = Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        protected override bool Compare(ref double a, ref double b) => a == b;
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref Utils.RefFieldValue<double>(obj, offset);
            a = Utils.Lerp(*(double*)prev, *(double*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.RefFieldValue<double>(obj, offset);
            *(double*)tempHistory = a;
            a = Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorBool : ValueTypeProcessor<bool>
    {
        protected override bool Compare(ref bool a, ref bool b) => a == b;
    }
    
    internal class ValueTypeProcessorEntitySharedReference : ValueTypeProcessor<EntitySharedReference>
    {
        protected override bool Compare(ref EntitySharedReference a, ref EntitySharedReference b) => a == b;
        
        internal override unsafe bool CompareAndWrite(object obj, int offset, byte* data)
        {
            //skip local ids
            var sharedRef = Utils.RefFieldValue<EntitySharedReference>(obj, offset);
            if (sharedRef.IsLocal)
                sharedRef = null;
            var latestRefPtr = (EntitySharedReference*)data;
            if (*latestRefPtr != sharedRef)
            {
                *latestRefPtr = sharedRef;
                return true;
            }
            return false;
        }
    }

    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private static readonly UIntPtr SizeU = new ((uint)sizeof(T));
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        protected override bool Compare(ref T a, ref T b)
        {
            fixed (void* ptrA = &a, ptrB = &b)
                return Utils.memcmp(ptrA, ptrB, SizeU) == 0;
        }

        internal override void SetInterpolation(object obj, int offset, byte* prev, byte* current,
            float fTimer)
        {
            ref var a = ref Utils.RefFieldValue<T>(obj, offset);
            a = _interpDelegate(*(T*)prev, *(T*)current, fTimer);
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate)
        {
            _interpDelegate = interpolationDelegate;
        }
    }
}