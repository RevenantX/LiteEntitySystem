using System;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    internal static class Utils
    {
#if UNITY_STANDALONE_WIN
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
#else
        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]                
#endif
        public static extern unsafe int memcmp([In] void* b1, [In] void* b2, [In] UIntPtr count);
    }
}