using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    internal abstract unsafe class ValueTypeProcessor
    {
        public static readonly Dictionary<Type, ValueTypeProcessor> Registered = new ();
        
        internal readonly int Size;

        protected ValueTypeProcessor(int size) => Size = size;

        internal abstract void InitSyncVar(InternalBaseClass obj, int offset, InternalEntity entity, ushort fieldId);
        internal abstract void SetFrom(InternalBaseClass obj, int offset, byte* data);
        internal abstract bool SetFromAndSync(InternalBaseClass obj, int offset, byte* data);
        internal abstract void WriteTo(InternalBaseClass obj, int offset, byte* data);
        internal abstract void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer);
        internal abstract void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
    }

    internal unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public ValueTypeProcessor() : base(sizeof(T)) { }

        internal override void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            throw new Exception($"This type: {typeof(T)} can't be interpolated");

        internal override void InitSyncVar(InternalBaseClass obj, int offset, InternalEntity entity, ushort fieldId) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).Init(entity, fieldId);

        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            *(T*)tempHistory = a;
            a = *(T*)historyA;
        }
        
        internal override void SetFrom(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.RefFieldValue<T>(obj, offset) = *(T*)data;

        internal override bool SetFromAndSync(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).SetFromAndSync(data);

        internal override void WriteTo(InternalBaseClass obj, int offset, byte* data) =>
            *(T*)data = RefMagic.RefFieldValue<T>(obj, offset);
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<int>(obj, offset) = Utils.Lerp(*(int*)prev, *(int*)current, fTimer);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<int>(obj, offset);
            *(int*)tempHistory = a;
            a = Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<long>(obj, offset) = Utils.Lerp(*(long*)prev, *(long*)current, fTimer);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<long>(obj, offset);
            *(long*)tempHistory = a;
            a = Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime);
        }
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<float>(obj, offset) = Utils.Lerp(*(float*)prev, *(float*)current, fTimer);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<float>(obj, offset);
            *(float*)tempHistory = a;
            a = Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime);
        }
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<double>(obj, offset) = Utils.Lerp(*(double*)prev, *(double*)current, fTimer);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<double>(obj, offset);
            *(double*)tempHistory = a;
            a = Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime);
        }
    }

    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<T>(obj, offset) = _interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev;
        
        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<T>(obj, offset);
            *(T*)tempHistory = a;
            a = _interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA;
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate)
        {
            _interpDelegate = interpolationDelegate;
        }
    }
}