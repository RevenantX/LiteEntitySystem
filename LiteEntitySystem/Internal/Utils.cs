using System;
using System.Collections.Generic;
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
                arr = new T[count > 8 ? count : 8];
            else if (count >= arr.Length)
                Array.Resize(ref arr, count*2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsBitSet(byte[] byteArray, int offset, int bitNumber) =>
            (byteArray[offset + bitNumber / 8] & (1 << bitNumber % 8)) != 0;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool IsBitSet(byte* byteArray, int bitNumber) =>
            (byteArray[bitNumber / 8] & (1 << bitNumber % 8)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Lerp(long a, long b, float t) => (long)(a + (b - a) * t);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Lerp(int a, int b, float t) => (int)(a + (b - a) * t);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Lerp(double a, double b, float t) => a + (b - a) * t;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort LerpSequence(ushort seq1, ushort seq2, float t) =>
            (ushort)((seq1 + Math.Floor(SequenceDiff(seq2, seq1) * t)) % MaxSequence);

        private const int MaxSequence = 65536;
        private const int MaxSeq2 = MaxSequence / 2;
        private const int MaxSeq15 = MaxSequence + MaxSeq2;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SequenceDiff(ushort newer, ushort older) => (newer - older + MaxSeq15) % MaxSequence - MaxSeq2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T CreateDelegateHelper<T>(this MethodInfo method) where T : Delegate => (T)method.CreateDelegate(typeof(T));
        
        public static Stack<Type> GetBaseTypes(Type ofType, Type until, bool includeSelf)
        {
            var resultTypes = new Stack<Type>();
            if(!includeSelf)
                ofType = ofType.BaseType;
            while (ofType != until && ofType != null)
            {
                resultTypes.Push(ofType);
                ofType = ofType.BaseType;
            }
            return resultTypes;
        }

        //field flags that used in LES
        internal static FieldInfo[] GetProcessedFields(Type t) =>
            t.GetFields(BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly |
                        BindingFlags.Static);

        internal static bool IsRemoteCallType(Type ft)
        {
            if (ft == typeof(RemoteCall))
                return true;
            if (!ft.IsGenericType)
                return false;
            var genericTypeDef = ft.GetGenericTypeDefinition();
            return genericTypeDef == typeof(RemoteCall<>) || genericTypeDef == typeof(RemoteCallSpan<>);
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
                : (Marshal.ReadInt32(fieldInfo.FieldHandle.Value + DotNetOffset) & 0xFFFFFF) + IntPtr.Size;
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