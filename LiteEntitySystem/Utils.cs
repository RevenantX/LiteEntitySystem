using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public static class Utils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float MoveTowards(float current, float target, float maxDelta) =>
            Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int WriteStruct<T>(this Span<byte> data, T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                *(T*) rawData = value;
            return sizeof(T);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int ReadStruct<T>(this ReadOnlySpan<byte> data, out T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                value = *(T*)rawData;
            return sizeof(T);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T ReadStruct<T>(this ReadOnlySpan<byte> data) where T : unmanaged
        {
            fixed (byte* rawData = data) 
                return *(T*)rawData;
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
        public static unsafe int GetEnumValueInt<T>(this T e) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: return *(byte*)&e;
                case 2: return *(short*)&e;
                case 4: return *(int*)&e;
                case 8: throw new Exception("Trying to get int value from long enum");
            }
            return -1;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool HasFlagFast<T>(this SyncVar<T> e, T flag) where T : unmanaged, Enum
        {
            var v = e.Value;
            switch (sizeof(T))
            {
                case 1: return (*(byte*)&v  & *(byte*)&flag)  != 0;
                case 2: return (*(short*)&v & *(short*)&flag) != 0;
                case 4: return (*(int*)&v   & *(int*)&flag)   != 0;
                case 8: return (*(long*)&v  & *(long*)&flag)  != 0;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe long GetEnumValue<T>(this SyncVar<T> e) where T : unmanaged, Enum
        {
            var v = e.Value;
            switch (sizeof(T))
            {
                case 1: return *(byte*)&v;
                case 2: return *(short*)&v;
                case 4: return *(int*)&v;
                case 8: return *(long*)&v;
            }
            return -1;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool FastEquals<T>(ref T a, ref T b) where T : unmanaged
        {
            fixed (T* ta = &a, tb = &b)
            {
                byte* x1=(byte*)ta, x2=(byte*)tb;
                int l = sizeof(T);
                for (int i=0; i < l/8; i++, x1+=8, x2+=8)
                    if (*(long*)x1 != *(long*)x2) return false;
                if ((l & 4)!=0) { if (*(int*)x1!=*(int*)x2) return false; x1+=4; x2+=4; }
                if ((l & 2)!=0) { if (*(short*)x1!=*(short*)x2) return false; x1+=2; x2+=2; }
                return (l & 1) == 0 || *x1 == *x2;
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool FastEquals<T>(ref T a, byte *x2) where T : unmanaged
        {
            fixed (T* ta = &a)
            {
                byte* x1=(byte*)ta;
                int l = sizeof(T);
                for (int i=0; i < l/8; i++, x1+=8, x2+=8)
                    if (*(long*)x1 != *(long*)x2) return false;
                if ((l & 4)!=0) { if (*(int*)x1!=*(int*)x2) return false; x1+=4; x2+=4; }
                if ((l & 2)!=0) { if (*(short*)x1!=*(short*)x2) return false; x1+=2; x2+=2; }
                return (l & 1) == 0 || *x1 == *x2;
            }
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
        public static float InvLerp(float a, float b, float v) => Math.Clamp((v - a) / (b - a), 0f, 1f);

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
        
        internal static Stack<Type> GetBaseTypes(Type ofType, Type until, bool includeSelf)
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
        internal static FieldInfo[] GetProcessedFields(Type t)
        {
            var fArr = t.GetFields(BindingFlags.Instance |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly |
                                   BindingFlags.Static);
            Array.Sort(fArr, (f1, f2) => string.Compare(f1.Name, f2.Name, StringComparison.InvariantCulture));
            return fArr;
        }

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
        
        public static readonly ThreadLocal<UTF8Encoding> Encoding = new (() => new UTF8Encoding(false, true));

        private static readonly int MonoOffset = IntPtr.Size * 3;
        private static readonly int DotNetOffset = IntPtr.Size + 4;
        private static readonly bool IsMono;

        internal static int GetFieldOffset(FieldInfo fieldInfo)
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