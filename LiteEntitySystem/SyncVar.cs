using System;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [Flags]
    public enum SyncFlags : byte
    {
        None                = 0,
        Interpolated        = 1,
        LagCompensated      = 1 << 1,
        OnlyForOtherPlayers = 1 << 2,
        OnlyForOwner        = 1 << 3,
        AlwaysRollback      = 1 << 4,
        NeverRollBack       = 1 << 5
    }
    
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class SyncVarFlags : Attribute
    {
        internal readonly SyncFlags Flags;

        public SyncVarFlags(SyncFlags flags)
        {
            Flags = flags;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVar<T> : IEquatable<T>, IEquatable<SyncVar<T>> where T : unmanaged
    {
        public T Value;
        internal byte FieldId;

        public SyncVar(T value)
        {
            Value = value;
            FieldId = 0;
        }

        public static implicit operator T(SyncVar<T> sv) => sv.Value;
        
        public static implicit operator SyncVar<T>(T v) => new() { Value = v };

        public override string ToString() => Value.ToString();

        public override int GetHashCode() => Value.GetHashCode();

        public override bool Equals(object o) => o is SyncVar<T> sv && Utils.FastEquals(ref sv.Value, ref Value);
        
        public static bool operator==(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a.Value, ref b.Value);

        public static bool operator!=(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a.Value, ref b.Value) == false;
        
        public static bool operator==(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b.Value);
        
        public static bool operator!=(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b.Value) == false;

        public bool Equals(T v) => Utils.FastEquals(ref Value, ref v);
        
        public bool Equals(SyncVar<T> tv) => Utils.FastEquals(ref Value, ref tv.Value);
    }
}