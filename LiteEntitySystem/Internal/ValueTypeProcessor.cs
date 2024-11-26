using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;
    
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
        internal abstract int GetHashCode(InternalBaseClass obj, int offset);
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
            ref var a = ref RefMagic.RefFieldValue<SyncVar<T>>(obj, offset);
            *(T*)tempHistory = a;
            a.SetDirect(*(T*)historyA);
        }
        
        internal override void SetFrom(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).SetDirect(*(T*)data);

        internal override bool SetFromAndSync(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).SetFromAndSync(data);

        internal override void WriteTo(InternalBaseClass obj, int offset, byte* data) =>
            *(T*)data = RefMagic.RefFieldValue<SyncVar<T>>(obj, offset);

        internal override int GetHashCode(InternalBaseClass obj, int offset) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).GetHashCode();
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<SyncVar<int>>(obj, offset).SetDirect(Utils.Lerp(*(int*)prev, *(int*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<SyncVar<int>>(obj, offset);
            *(int*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime));
        }
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<SyncVar<long>>(obj, offset).SetDirect(Utils.Lerp(*(long*)prev, *(long*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<SyncVar<long>>(obj, offset);
            *(long*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime));
        }
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<SyncVar<float>>(obj, offset).SetDirect(Utils.Lerp(*(float*)prev, *(float*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<SyncVar<float>>(obj, offset);
            *(float*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime));
        }
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<SyncVar<double>>(obj, offset).SetDirect(Utils.Lerp(*(double*)prev, *(double*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<SyncVar<double>>(obj, offset);
            *(double*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime));
        }
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).SetDirect(_interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev);
        
        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref RefMagic.RefFieldValue<SyncVar<T>>(obj, offset);
            *(T*)tempHistory = a;
            a.SetDirect(_interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA);
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}