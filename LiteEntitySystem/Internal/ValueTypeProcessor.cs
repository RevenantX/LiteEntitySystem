using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace LiteEntitySystem.Internal
{
    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;

    internal abstract unsafe class ValueTypeProcessor
    {
        public static readonly Dictionary<Type, ValueTypeProcessor> Registered = new();

        internal readonly int Size;

        protected ValueTypeProcessor(int size) => Size = size;

        internal abstract void InitSyncVar(InternalBaseClass obj, int[] offsetMap, InternalEntity entity, ushort fieldId);
        internal abstract void SetFrom(InternalBaseClass obj, int[] offsetMap, byte* data);
        internal abstract bool SetFromAndSync(InternalBaseClass obj, int[] offsetMap, byte* data);
        internal abstract void WriteTo(InternalBaseClass obj, int[] offsetMap, byte* data);
        internal abstract void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer);
        internal abstract void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
        internal abstract int GetHashCode(InternalBaseClass obj, int[] offsetMap);
        internal abstract string ToString(InternalBaseClass obj, int[] offsetMap);
    }

    internal unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public ValueTypeProcessor() : base(sizeof(T)) { }

        internal override void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer) =>
            throw new Exception($"This type: {typeof(T)} can't be interpolated");

        internal override void InitSyncVar(InternalBaseClass obj, int[] offsetMap, InternalEntity entity, ushort fieldId)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            var sv = RefMagic.GetFieldValue<SyncVar<T>>(owner, offset);
            sv.Init(entity, fieldId);
            RefMagic.SetFieldValue(owner, offset, sv);
        }

        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];

            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(owner, offset, *(T*)historyA, out *(T*)tempHistory);
        }

        internal override void SetFrom(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];

            RefMagic.SyncVarSetDirect<T, SyncVar<T>>(owner, offset, *(T*)data);
        }

        internal override bool SetFromAndSync(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            
            return RefMagic.SyncVarSetFromAndSync<T, SyncVar<T>>(owner, offset, ref *(T*)data);
        }


        internal override void WriteTo(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            *(T*)data = Utils.GetSyncVar<T>(obj, offsetMap);
        }

        internal override int GetHashCode(InternalBaseClass obj, int[] offsetMap)
        {
            return Utils.GetSyncVar<T>(obj, offsetMap).GetHashCode();
        }


        internal override string ToString(InternalBaseClass obj, int[] offsetMap)
        {
            return Utils.GetSyncVar<T>(obj, offsetMap).ToString();
        }
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirect<int, SyncVar<int>>(owner, offset, Utils.Lerp(*(int*)prev, *(int*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirectAndStorePrev<int, SyncVar<int>>(owner, offset,
                Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime), out *(int*)tempHistory);
        }
    }

    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirect<long, SyncVar<long>>(owner, offset, Utils.Lerp(*(long*)prev, *(long*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirectAndStorePrev<long, SyncVar<long>>(owner, offset,
                Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime), out *(long*)tempHistory);
        }
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];

            RefMagic.SyncVarSetDirect<float, SyncVar<float>>(owner, offset, Utils.Lerp(*(float*)prev, *(float*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirectAndStorePrev<float, SyncVar<float>>(owner, offset,
                Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime), out *(float*)tempHistory);
        }
    }

    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirect<double, SyncVar<double>>(owner, offset, Utils.Lerp(*(double*)prev, *(double*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirectAndStorePrev<double, SyncVar<double>>(owner, offset,
                Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime), out *(double*)tempHistory);
        }
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirect<T, SyncVar<T>>(owner, offset, _interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev);
        }

        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            var owner = Utils.GetSyncVarOwner(obj, offsetMap);
            var offset = offsetMap[offsetMap.Length - 1];
            RefMagic.SyncVarSetDirectAndStorePrev<T, SyncVar<T>>(owner, offset,
                _interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA, out *(T*)tempHistory);
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}