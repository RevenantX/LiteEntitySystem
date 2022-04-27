using System;
using System.Runtime.CompilerServices;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResizeIfFull<T>(ref T[] arr, int count)
        {
            if (count == arr.Length)
                Array.Resize(ref arr, count*2);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResizeOrCreate<T>(ref T[] arr, int count)
        {
            if (arr == null)
                arr = new T[count];
            else if (count == arr.Length)
                Array.Resize(ref arr, count*2);
        }

        public static void DebugProfileBegin(string name)
        {
#if UNITY_2020_1_OR_NEWER
            UnityEngine.Profiling.Profiler.BeginSample(name);
#endif
        }

        public static void DebugProfileEnd()
        {
#if UNITY_2020_1_OR_NEWER
            UnityEngine.Profiling.Profiler.EndSample();
#endif       
        }
    }
}