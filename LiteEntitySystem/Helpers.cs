using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public static class Helpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int WriteStructAndReturnSize<T>(this Span<byte> data, T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                *(T*) rawData = value;
            return sizeof(T);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int ReadStructAndReturnSize<T>(this ReadOnlySpan<byte> data, out T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                value = *(T*)rawData;
            return sizeof(T);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void WriteStruct<T>(this Span<byte> data, T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                *(T*) rawData = value;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T ReadStruct<T>(this ReadOnlySpan<byte> data) where T : unmanaged
        {
            fixed (byte* rawData = data)
                return *(T*)rawData;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T ReadStruct<T>(this Span<byte> data) where T : unmanaged
        {
            fixed (byte* rawData = data)
                return *(T*)rawData;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void ReadStruct<T>(this Span<byte> data, out T result) where T : unmanaged
        {
            fixed (byte* rawData = data)
                result = *(T*)rawData;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void ReadStruct<T>(this ReadOnlySpan<byte> data, out T result) where T : unmanaged
        {
            fixed (byte* rawData = data)
                result = *(T*)rawData;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int SizeOfStruct<T>() where T : unmanaged
        {
            return sizeof(T);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool HasFlagFast<T>(this T e, T flag) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: return (*(byte*)&e  & *(byte*)&flag)  != 0;
                case 2: return (*(short*)&e & *(short*)&flag) != 0;
                case 4: return (*(int*)&e   & *(int*)&flag)   != 0;
                case 8: return (*(long*)&e  & *(long*)&flag)  != 0;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe long GetEnumValue<T>(this T e) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: return *(byte*)&e;
                case 2: return *(short*)&e;
                case 4: return *(int*)&e;
                case 8: return *(long*)&e;
            }
            return -1;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool HasFlagFast<T>(this SyncVar<T> e, T flag) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: return (*(byte*)&e.Value  & *(byte*)&flag)  != 0;
                case 2: return (*(short*)&e.Value & *(short*)&flag) != 0;
                case 4: return (*(int*)&e.Value   & *(int*)&flag)   != 0;
                case 8: return (*(long*)&e.Value  & *(long*)&flag)  != 0;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe long GetEnumValue<T>(this SyncVar<T> e) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: return *(byte*)&e.Value;
                case 2: return *(short*)&e.Value;
                case 4: return *(int*)&e.Value;
                case 8: return *(long*)&e.Value;
            }
            return -1;
        }
        
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
            return (ushort)((seq1 + Math.Floor(SequenceDiff(seq2, seq1) * t)) % MaxSequence);
        }

        private const int MaxSequence = 65536;
        private const int MaxSeq2 = MaxSequence / 2;
        private const int MaxSeq15 = MaxSequence + MaxSeq2;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SequenceDiff(ushort newer, ushort older)
        {
            return (newer - older + MaxSeq15) % MaxSequence - MaxSeq2;
        }
    }
}