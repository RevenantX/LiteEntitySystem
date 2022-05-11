using System;

namespace LiteEntitySystem
{
    [Flags]
    public enum SyncFlags : byte
    {
        None = 0,
        Interpolated = 1,
        LagCompensated = 1 << 1
    }
    
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncVar : Attribute
    {
        internal readonly SyncFlags Flags;
        internal readonly string MethodName;

        public SyncVar()
        {
            
        }
        
        public SyncVar(SyncFlags flags)
        {
            Flags = flags;
        }
        
        public SyncVar(string methodName)
        {
            MethodName = methodName;
        }
    }
}