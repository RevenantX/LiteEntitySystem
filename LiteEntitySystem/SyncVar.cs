using System;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Synchronization flags. 
    /// </summary>
    [Flags]
    public enum SyncFlags : ushort
    {
        None                = 0,
        
        /// <summary>
        /// Is value interpolated inside VisualUpdate and in LagCompensation checks
        /// best use for Position, Rotation
        /// </summary>
        Interpolated        = 1,
        
        /// <summary>
        /// Is value lag compensated (returned in history) when EnableLagCompensation called
        /// for hit checks on server and on client in rollback state 
        /// </summary>
        LagCompensated      = 1 << 1,
        
        /// <summary>
        /// Value synchronized only for non owners
        /// </summary>
        OnlyForOtherPlayers = 1 << 2,
        
        /// <summary>
        /// Value synchronized only for owner
        /// </summary>
        OnlyForOwner        = 1 << 3,
        
        /// <summary>
        /// Always rollback value even when entity is not owned
        /// useful for enemy health and damage prediction
        /// </summary>
        AlwaysRollback      = 1 << 4,
        
        /// <summary>
        /// Never rollback value even when entity is owned
        /// </summary>
        NeverRollBack       = 1 << 5,
        
        ///<summary>Toggleable sync group 1. Can include SyncVars and RPCs.</summary>
        SyncGroup1          = 1 << 6,
        
        ///<summary>Toggleable sync group 2. Can include SyncVars and RPCs.</summary>
        SyncGroup2          = 1 << 7,
        
        ///<summary>Toggleable sync group 3. Can include SyncVars and RPCs.</summary>
        SyncGroup3          = 1 << 8,
        
        ///<summary>Toggleable sync group 4. Can include SyncVars and RPCs.</summary>
        SyncGroup4          = 1 << 9,
        
        ///<summary>Toggleable sync group 5. Can include SyncVars and RPCs.</summary>
        SyncGroup5          = 1 << 10
    }
    
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class SyncVarFlags : Attribute
    {
        public readonly SyncFlags Flags;
        public SyncVarFlags(SyncFlags flags) => Flags = flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVar<T> : ISyncVar<T>, IEquatable<T>, IEquatable<SyncVar<T>> where T : unmanaged
    {
        private T _value;
        internal ushort FieldId;
        internal InternalEntity Container;
        
        void ISyncVar<T>.SvSetDirect(T value) => _value = value;
        
        void ISyncVar<T>.SvSetDirectAndStorePrev(T value, out T prevValue)
        {
            prevValue = _value;
            _value = value;
        }
        
        bool ISyncVar<T>.SvSetFromAndSync(ref T value)
        {
            if (!Utils.FastEquals(ref _value, ref value))
            {
                // ReSharper disable once SwapViaDeconstruction
                var tmp = _value;
                _value = value;
                value = tmp;
                return true;
            }
            return false;
        }
        
        public T Value
        {
            get => _value;
            set
            {
                if (Container != null && !Utils.FastEquals(ref value, ref _value))
                    Container.EntityManager.EntityFieldChanged(Container, FieldId, ref value);
                _value = value;
            }
        }

        internal void Init(InternalEntity container, ushort fieldId)
        {
            Container = container;
            FieldId = fieldId;
            Container?.EntityManager.EntityFieldChanged(Container, FieldId, ref _value);
        }
        
        public static implicit operator T(SyncVar<T> sv) => sv._value;

        public override string ToString() => _value.ToString();

        public override int GetHashCode() => _value.GetHashCode();

        public override bool Equals(object o) => o is SyncVar<T> sv && Utils.FastEquals(ref sv._value, ref _value);
        
        public static bool operator==(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a._value, ref b._value);

        public static bool operator!=(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a._value, ref b._value) == false;
        
        public static bool operator==(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b._value);
        
        public static bool operator!=(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b._value) == false;
        
        public static bool operator==(SyncVar<T> a, T b) => Utils.FastEquals(ref b, ref a._value);
        
        public static bool operator!=(SyncVar<T> a, T b) => Utils.FastEquals(ref b, ref a._value) == false;

        public bool Equals(T v) => Utils.FastEquals(ref _value, ref v);
        
        public bool Equals(SyncVar<T> tv) => Utils.FastEquals(ref _value, ref tv._value);
    }
}