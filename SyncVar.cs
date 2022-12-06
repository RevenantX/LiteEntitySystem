using System;
using System.Runtime.InteropServices;

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
        AlwaysPredict       = 1 << 4
    }
    
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncVarFlags : Attribute
    {
        internal readonly SyncFlags Flags;

        public SyncVarFlags(SyncFlags flags)
        {
            Flags = flags;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVar<T> where T : unmanaged
    {
        public T Value;

        public static implicit operator T(SyncVar<T> sv)
        {
            return sv.Value;
        }
        
        public static implicit operator SyncVar<T>(T v)
        {
            return new SyncVar<T> { Value = v };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVarWithNotify<T> where T : unmanaged
    {
        public T Value;
        internal byte FieldId;
        
        public static implicit operator T(SyncVarWithNotify<T> sv)
        {
            return sv.Value;
        }
        
        public static implicit operator SyncVarWithNotify<T>(T v)
        {
            return new SyncVarWithNotify<T> { Value = v };
        }
    }
}