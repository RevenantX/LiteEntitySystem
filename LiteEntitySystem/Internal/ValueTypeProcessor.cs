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
        internal readonly int Size;

        protected ValueTypeProcessor(int size)
        {
            Size = size;
        }
        
        internal abstract bool CompareAndWrite(object obj, int offset, byte* data);
        internal abstract void SetFrom(object obj, int offset, byte* data);
        internal abstract bool SetFromAndSync(object obj, int offset, byte* data);
        internal abstract void WriteTo(object obj, int offset, byte* data);
        internal abstract void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer);
        internal abstract void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
    }

    internal abstract unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        protected ValueTypeProcessor() : base(sizeof(T))
        {
        }

        internal override void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            throw new Exception($"This type: {typeof(T)} can't be interpolated");
        }
        
        internal override void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            *(T*)tempHistory = a;
            a = *(T*)historyA;
        }
        
        internal override void SetFrom(object obj, int offset, byte* data)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            a = *(T*)data;
        }

        internal override bool SetFromAndSync(object obj, int offset, byte* data)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            if (!Utils.FastEquals(a, *(T*)data))
            {
                var temp = a;
                a = *(T*)data;
                *(T*)data = temp;
                return true;
            }
            return false;
        }

        internal override void WriteTo(object obj, int offset, byte* data)
        {
            *(T*)data = RefMagic.RefFieldValue<T>(obj, offset);
        }
        
        internal override bool CompareAndWrite(object obj, int offset, byte* data)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            if (Utils.FastEquals(a, *(T*)data))
                return false;
            *(T*)data = a;
            return true;
        }
    }
    
    internal unsafe class BasicTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged, IEquatable<T>
    {
        internal override bool SetFromAndSync(object obj, int offset, byte* data)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            if (!a.Equals(*(T*)data))
            {
                var temp = a;
                a = *(T*)data;
                *(T*)data = temp;
                return true;
            }
            return false;
        }

        internal override bool CompareAndWrite(object obj, int offset, byte* data)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            if (a.Equals(*(T*)data))
                return false;
            *(T*)data = a;
            return true;
        }
    }

    internal class ValueTypeProcessorInt : BasicTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref RefMagic.RefFieldValue<int>(obj, offset);
            a = Utils.Lerp(*(int*)prev, *(int*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<int>(obj, offset);
            *(int*)tempHistory = a;
            a = Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorLong : BasicTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref RefMagic.RefFieldValue<long>(obj, offset);
            a = Utils.Lerp(*(long*)prev, *(long*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<long>(obj, offset);
            *(long*)tempHistory = a;
            a = Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime);
        }
    }

    internal class ValueTypeProcessorFloat : BasicTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref RefMagic.RefFieldValue<float>(obj, offset);
            a = Utils.Lerp(*(float*)prev, *(float*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<float>(obj, offset);
            *(float*)tempHistory = a;
            a = Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorDouble : BasicTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref RefMagic.RefFieldValue<double>(obj, offset);
            a = Utils.Lerp(*(double*)prev, *(double*)current, fTimer);
        }
        internal override unsafe void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<double>(obj, offset);
            *(double*)tempHistory = a;
            a = Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorEntitySharedReference : BasicTypeProcessor<EntitySharedReference>
    {
        internal override unsafe bool CompareAndWrite(object obj, int offset, byte* data)
        {
            //skip local ids
            var sharedRef = RefMagic.RefFieldValue<EntitySharedReference>(obj, offset);
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
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(object obj, int offset, byte* prev, byte* current, float fTimer)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            a = _interpDelegate(*(T*)prev, *(T*)current, fTimer);
        }
        
        internal override void LoadHistory(object obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            *(T*)tempHistory = a;
            a = _interpDelegate != null ? _interpDelegate(*(T*)historyA, *(T*)historyB, lerpTime) : *(T*)historyA;
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate)
        {
            _interpDelegate = interpolationDelegate;
        }
    }
}