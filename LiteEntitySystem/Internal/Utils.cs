using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public static class Utils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResizeIfFull<T>(ref T[] arr, int count)
        {
            if (count >= arr.Length)
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Lerp(long a, long b, float t)
        {
            return (long)(a + (b - a) * t);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Lerp(int a, int b, float t)
        {
            return (int)(a + (b - a) * t);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Lerp(double a, double b, float t)
        {
            return a + (b - a) * t;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort LerpSequence(ushort seq1, ushort seq2, float t)
        {
            return (ushort)((seq1 + Math.Round(SequenceDiff(seq2, seq1) * t)) % MaxSequence);
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
        public static ref U RefFieldValue<U>(object obj, int offset)
        {
#if UNITY_2021_2_OR_NEWER
            return ref RefMagic.RefFieldValueMono<U>(obj, offset);
#else
            return ref RefMagic.RefFieldValueDotNet<U>(obj, offset + IntPtr.Size);
#endif
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T CreateDelegateHelper<T>(this MethodInfo method) where T : Delegate
        {
            return (T)method.CreateDelegate(typeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Action<T> CreateSelfDelegate<T>(this MethodInfo mi)
        {
            return mi.CreateDelegateHelper<Action<T>>();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Action<T, TArgument> CreateSelfDelegate<T, TArgument>(this MethodInfo mi) where TArgument : unmanaged
        {
            return mi.CreateDelegateHelper<Action<T, TArgument>>();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SpanAction<T, TArgument> CreateSelfDelegateSpan<T, TArgument>(this MethodInfo mi) where TArgument : unmanaged
        {
            return mi.CreateDelegateHelper<SpanAction<T, TArgument>>();
        }
    }
}