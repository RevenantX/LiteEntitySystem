using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        
        private class TestOffset
        {
            public readonly uint TestValue = 0xDEADBEEF;
        }
        
        /*
        Offsets
        [StructLayout(LayoutKind.Explicit)]
        public unsafe struct DotnetClassField
        {
            [FieldOffset(0)] private readonly void* _pMTOfEnclosingClass;
            [FieldOffset(8)] private readonly uint _dword1;
            [FieldOffset(12)] private readonly uint _dword2;
            public int Offset => (int) (_dword2 & 0x7FFFFFF);
        }

        public unsafe struct MonoClassField
        {
            private void *_type;
            private void *_name;
            private	void *_parent_and_flags;
            public int Offset;
        }
        */

        private static readonly int MonoOffset = IntPtr.Size * 3;
        private static readonly int DotNetOffset = IntPtr.Size + 4;
        private static readonly bool IsMono;

        public static int GetFieldOffset(FieldInfo fieldInfo)
        {
            //build offsets in runtime metadata
            if(fieldInfo.DeclaringType != null)
                RuntimeHelpers.RunClassConstructor(fieldInfo.DeclaringType.TypeHandle);
            return IsMono
                ? Marshal.ReadInt32(fieldInfo.FieldHandle.Value + MonoOffset)
                : Marshal.ReadInt32(fieldInfo.FieldHandle.Value + DotNetOffset) & 0xFFFFFF + IntPtr.Size;
        }

        static Utils()
        {            
            IsMono = Type.GetType("Mono.Runtime") != null
                     || RuntimeInformation.OSDescription.Contains("android")
                     || RuntimeInformation.OSDescription.Contains("ios");
            
            //check field offset
            var field = typeof(TestOffset).GetField("TestValue");
            int offset = GetFieldOffset(field);
            var to = new TestOffset();
            if (RefMagic.RefFieldValue<uint>(to, offset) != to.TestValue)
                Logger.LogError("Unknown native field offset");
        }
    }
}