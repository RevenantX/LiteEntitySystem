using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    internal static class Utils
    {
#if UNITY_STANDALONE_WIN || _WINDOWS
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
            else if (count >= arr.Length)
                Array.Resize(ref arr, count*2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBitSet(byte[] byteArray, int offset, int bitNumber)
        {
            return (byteArray[offset + bitNumber / 8] & (1 << bitNumber % 8)) != 0;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool IsBitSet(byte* byteArray, int bitNumber)
        {
            return (byteArray[bitNumber / 8] & (1 << bitNumber % 8)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
        
        private const int MaxSequence = 65536;
        private const int MaxSeq2 = MaxSequence / 2;
        private const int MaxSeq15 = MaxSequence + MaxSeq2;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SequenceDiff(ushort newer, ushort older)
        {
            return (newer - older + MaxSeq15) % MaxSequence - MaxSeq2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Minimal<TIndex, T>(this SortedList<TIndex, T> sortedList)
        {
            return sortedList[sortedList.Keys[0]];
        }
    }
    
    internal sealed class SequenceComparer : IComparer<ushort>
    {
        public int Compare(ushort x, ushort y)
        {
            return Utils.SequenceDiff(x, y);
        }
    }
}