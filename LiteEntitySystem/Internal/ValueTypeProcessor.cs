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
        internal abstract string ToString(InternalBaseClass obj, int offset);
    }

    internal unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public ValueTypeProcessor() : base(sizeof(T)) { }

        internal override void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            throw new Exception($"This type: {typeof(T)} can't be interpolated");

        internal override void InitSyncVar(InternalBaseClass obj, int offset, InternalEntity entity, ushort fieldId)
        {
            var sv = RefMagic.GetFieldValue<SyncVar<T>>(obj, offset);
            sv.Init(entity, fieldId);
            RefMagic.SetFieldValue(obj, offset, sv);
        }
        
        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(obj, offset, *(T*)historyA, out *(T*)tempHistory);
        
        internal override void SetFrom(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.SyncVarSetDirect<T, SyncVar<T>>(obj, offset, *(T*)data);

        internal override bool SetFromAndSync(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.SyncVarSetFromAndSync<T, SyncVar<T>>(obj, offset, ref *(T*)data);

        internal override void WriteTo(InternalBaseClass obj, int offset, byte* data) =>
            *(T*)data = RefMagic.GetFieldValue<SyncVar<T>>(obj, offset);

        internal override int GetHashCode(InternalBaseClass obj, int offset) =>
            RefMagic.GetFieldValue<SyncVar<T>>(obj, offset).GetHashCode();
        
        internal override string ToString(InternalBaseClass obj, int offset) =>
            RefMagic.GetFieldValue<SyncVar<T>>(obj, offset).ToString();
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.SyncVarSetDirect<int, SyncVar<int>>(obj, offset, Utils.Lerp(*(int*)prev, *(int*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<int, SyncVar<int>>(obj, offset,
                Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime), out *(int*)tempHistory);
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.SyncVarSetDirect<long, SyncVar<long>>(obj, offset, Utils.Lerp(*(long*)prev, *(long*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<long, SyncVar<long>>(obj, offset,
                Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime), out *(long*)tempHistory);
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.SyncVarSetDirect<float, SyncVar<float>>(obj, offset, Utils.Lerp(*(float*)prev, *(float*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<float, SyncVar<float>>(obj, offset,
                Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime), out *(float*)tempHistory);
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.SyncVarSetDirect<double, SyncVar<double>>(obj, offset, Utils.Lerp(*(double*)prev, *(double*)current, fTimer));
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<double, SyncVar<double>>(obj, offset,
                Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime), out *(double*)tempHistory);
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int offset, byte* prev, byte* current, float fTimer) =>
            RefMagic.SyncVarSetDirect<T, SyncVar<T>>(obj, offset, _interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev);

        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(obj, offset,
                _interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA, out *(T*)tempHistory);

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}