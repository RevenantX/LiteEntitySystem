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
        internal abstract void SetInterpValue(InternalBaseClass obj, int offset, byte* data);
        internal abstract void SetInterpValueFromCurrentValue(InternalBaseClass obj, int offset);
        internal abstract void WriteTo(InternalBaseClass obj, int offset, byte* data);
        internal abstract void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
        internal abstract int GetHashCode(InternalBaseClass obj, int offset);
        internal abstract string ToString(InternalBaseClass obj, int offset);
    }

    internal unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public ValueTypeProcessor() : base(sizeof(T)) { }

        internal virtual T GetInterpolatedValue(T prev, T current, float t) => current;

        internal sealed override void InitSyncVar(InternalBaseClass obj, int offset, InternalEntity entity, ushort fieldId)
        {
            var sv = RefMagic.GetFieldValue<SyncVar<T>>(obj, offset);
            sv.Init(entity, fieldId);
            RefMagic.SetFieldValue(obj, offset, sv);
        }
        
        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(obj, offset, *(T*)historyA, out *(T*)tempHistory);
        
        internal sealed override void SetFrom(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.SyncVarSetDirect<T, SyncVar<T>>(obj, offset, *(T*)data);

        internal sealed override bool SetFromAndSync(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.SyncVarSetFromAndSync<T, SyncVar<T>>(obj, offset, ref *(T*)data);

        internal sealed override void SetInterpValue(InternalBaseClass obj, int offset, byte* data) =>
            RefMagic.SyncVarSetInterp<T, SyncVar<T>>(obj, offset, *(T*)data);
        
        internal sealed override void SetInterpValueFromCurrentValue(InternalBaseClass obj, int offset) =>
            RefMagic.SyncVarSetInterp<T, SyncVar<T>>(obj, offset, RefMagic.GetFieldValue<SyncVar<T>>(obj, offset));

        internal sealed override void WriteTo(InternalBaseClass obj, int offset, byte* data) =>
            *(T*)data = RefMagic.GetFieldValue<SyncVar<T>>(obj, offset);

        internal sealed override int GetHashCode(InternalBaseClass obj, int offset) =>
            RefMagic.GetFieldValue<SyncVar<T>>(obj, offset).GetHashCode();
        
        internal sealed override string ToString(InternalBaseClass obj, int offset) =>
            RefMagic.GetFieldValue<SyncVar<T>>(obj, offset).ToString();
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override int GetInterpolatedValue(int prev, int current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<int, SyncVar<int>>(obj, offset,
                Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime), out *(int*)tempHistory);
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override long GetInterpolatedValue(long prev, long current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<long, SyncVar<long>>(obj, offset,
                Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime), out *(long*)tempHistory);
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override float GetInterpolatedValue(float prev, float current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<float, SyncVar<float>>(obj, offset,
                Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime), out *(float*)tempHistory);
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override double GetInterpolatedValue(double prev, double current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<double, SyncVar<double>>(obj, offset,
                Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime), out *(double*)tempHistory);
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override T GetInterpolatedValue(T prev, T current, float t) => _interpDelegate?.Invoke(prev, current, t) ?? current; 
        
        internal override void LoadHistory(InternalBaseClass obj, int offset, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(obj, offset,
                _interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA, out *(T*)tempHistory);

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}