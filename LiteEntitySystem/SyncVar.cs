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
    public unsafe struct SyncVar<T> where T : unmanaged
    {
        public T Value;
        internal byte FieldId;

        public SyncVar(T value)
        {
            Value = value;
            FieldId = 0;
        }

        public static implicit operator T(SyncVar<T> sv)
        {
            return sv.Value;
        }
        
        public static implicit operator SyncVar<T>(T v)
        {
            return new SyncVar<T> { Value = v };
        }

        public override string ToString()
        {
            return Value.ToString();
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }
        
        public override bool Equals(object o)
        {
            return this == (SyncVar<T>)o;
        }

        private static readonly int Size = sizeof(T);

        public static bool operator==(SyncVar<T> a, SyncVar<T> b)
        {
            return new ReadOnlySpan<byte>(&a.Value, Size).SequenceEqual(new ReadOnlySpan<byte>(&b.Value, Size));
        }
        
        public static bool operator!=(SyncVar<T> a, SyncVar<T> b)
        {
            return new ReadOnlySpan<byte>(&a.Value, Size).SequenceEqual(new ReadOnlySpan<byte>(&b.Value, Size)) == false;
        }
        
        public static bool operator==(T a, SyncVar<T> b)
        {
            return new ReadOnlySpan<byte>(&a, Size).SequenceEqual(new ReadOnlySpan<byte>(&b.Value, Size));
        }
        
        public static bool operator!=(T a, SyncVar<T> b)
        {
            return new ReadOnlySpan<byte>(&a, Size).SequenceEqual(new ReadOnlySpan<byte>(&b.Value, Size)) == false;
        }
    }
}