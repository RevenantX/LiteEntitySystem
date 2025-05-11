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

        internal override void InitSyncVar(InternalBaseClass obj, int offset, InternalEntity entity, ushort fieldId)
        {
            
            RefMagic.RefFieldValue<SyncVar<T>>(obj, offset).Init(entity, fieldId);
        }

        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            ref var a = ref RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]);
            *(T*)tempHistory = a;
            a.SetDirect(*(T*)historyA);
        }
        
        internal override void SetFrom(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(*(T*)data);
        }

        internal override bool SetFromAndSync(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            return RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]).SetFromAndSync(data);
        }
            

        internal override void WriteTo(InternalBaseClass obj, int[] offsetMap, byte* data)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            *(T*)data = RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]);
        }

        internal override int GetHashCode(InternalBaseClass obj, int[] offsetMap)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            return RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]).GetHashCode();
        }
            
        
        internal override string ToString(InternalBaseClass obj, int[] offsetMap)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            return RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]).ToString();
        }
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            RefMagic.RefFieldValue<SyncVar<int>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(Utils.Lerp(*(int*)prev, *(int*)current, fTimer));
        }
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

            ref var a = ref RefMagic.RefFieldValue<SyncVar<int>>(syncable, offsetMap[offsetMap.Length-1]);
            *(int*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime));
        }
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

            RefMagic.RefFieldValue<SyncVar<long>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(Utils.Lerp(*(long*)prev, *(long*)current, fTimer));
        }
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

           ref var a = ref RefMagic.RefFieldValue<SyncVar<long>>(syncable, offsetMap[offsetMap.Length-1]);
            *(long*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime));
        }
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

            RefMagic.RefFieldValue<SyncVar<float>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(Utils.Lerp(*(float*)prev, *(float*)current, fTimer));
        }
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            ref var a = ref RefMagic.RefFieldValue<SyncVar<float>>(syncable, offsetMap[offsetMap.Length-1]);
            *(float*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime));
        }
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override unsafe void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            RefMagic.RefFieldValue<SyncVar<double>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(Utils.Lerp(*(double*)prev, *(double*)current, fTimer));
        }
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

            ref var a = ref RefMagic.RefFieldValue<SyncVar<double>>(syncable, offsetMap[offsetMap.Length-1]);
            *(double*)tempHistory = a;
            a.SetDirect(Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime));
        }
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override void SetInterpolation(InternalBaseClass obj, int[] offsetMap, byte* prev, byte* current, float fTimer)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }

            RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]).SetDirect(_interpDelegate?.Invoke(*(T*)prev, *(T*)current, fTimer) ?? *(T*)prev);
        }
        
        internal override void LoadHistory(InternalBaseClass obj, int[] offsetMap, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime)
        {
            InternalBaseClass syncable = obj;
            for (int i = 0; i < offsetMap.Length-1; i++)
            {
                syncable = RefMagic.RefFieldValue<InternalBaseClass>(syncable, offsetMap[i]);
                if (syncable == null)
                    throw new NullReferenceException($"SyncVar at offset {offsetMap[i]} is null");
            }
            
            ref var a = ref RefMagic.RefFieldValue<SyncVar<T>>(syncable, offsetMap[offsetMap.Length-1]);
            *(T*)tempHistory = a;
            a.SetDirect(_interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA);
        }

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}