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
    public class SyncVar : Attribute
    {
        internal readonly SyncFlags Flags;

        public SyncVar()
        {
        }

        public SyncVar(SyncFlags flags)
        {
            Flags = flags;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVarWithNotify<T> where T : unmanaged
    {
        internal byte FieldId;
        
        public T Value;

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