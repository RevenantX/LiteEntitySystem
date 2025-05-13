using System;
using System.Collections.Generic;

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
            Utils.GetSyncableSyncVar<T>(obj, offsetMap).Init(entity, fieldId);
        }

        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var syncvar = ref Utils.GetSyncableSyncVar<T>(obj, offsetMap);

            *(T*)tempHistory = syncvar;
            syncvar.SetDirect(*(T*)historyA);
        }

        internal override void SetFrom(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            Utils.GetSyncableSyncVar<T>(obj, offsetMap).SetDirect(*(T*)data);
        }

        internal override bool SetFromAndSync(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            return Utils.GetSyncableSyncVar<T>(obj, offsetMap).SetFromAndSync(data);
        }


        internal override void WriteTo(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            *(T*)data = Utils.GetSyncableSyncVar<T>(obj, offsetMap);
        }

        internal override int GetHashCode(InternalBaseClass obj, int[] offsetMap)
        {
            return Utils.GetSyncableSyncVar<T>(obj, offsetMap).GetHashCode();
        }


        internal override string ToString(InternalBaseClass obj, int[] offsetMap)
        {
            return Utils.GetSyncableSyncVar<T>(obj, offsetMap).ToString();
        }
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            Utils.GetSyncableSyncVar<int>(obj, offsetMap).SetDirect(Utils.Lerp(*(int*)prev, *(int*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.GetSyncableSyncVar<int>(obj, offsetMap);
            *(int*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime));
        }
    }

    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            Utils.GetSyncableSyncVar<long>(obj, offsetMap).SetDirect(Utils.Lerp(*(long*)prev, *(long*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.GetSyncableSyncVar<long>(obj, offsetMap);
            *(long*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime));
        }
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var syncvar = Utils.GetSyncableSyncVar<float>(obj, offsetMap);
            var a = *(float*)prev;
            var b = *(float*)current;

            syncvar.SetDirect(Utils.Lerp(a, b, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref  Utils.GetSyncableSyncVar<float>(obj, offsetMap);
            *(float*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime));
        }
    }

    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            Utils.GetSyncableSyncVar<double>(obj, offsetMap).SetDirect(Utils.Lerp(*(double*)prev, *(double*)current, fTimer));
        }

        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref  Utils.GetSyncableSyncVar<double>(obj, offsetMap);
            *(double*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime));
        }
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            var syncvar = Utils.GetSyncableSyncVar<T>(obj, offsetMap);

            var a = *(T*)prev;
            var b = *(T*)current;

            syncvar.SetDirect(_interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev);
        }

        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            ref var a = ref Utils.GetSyncableSyncVar<T>(obj, offsetMap);
            *(T*)tempHistory = a;
            a.SetDirect(_interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA);
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}