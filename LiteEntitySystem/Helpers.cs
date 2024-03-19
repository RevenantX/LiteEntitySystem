using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public static class Helpers
    {
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
        
        public static unsafe T AsEnum<T>(this short x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(short*)&e = x;
            return e;
        }
        
        public static unsafe T AsEnum<T>(this ushort x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(ushort*)&e = x;
            return e;
        }

        public static unsafe T AsEnum<T>(this int x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(int*)&e = x;
            return e;
        }
        
        public static unsafe T AsEnum<T>(this uint x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(uint*)&e = x;
            return e;
        }

        public static unsafe T AsEnum<T>(this long x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(long*)&e = x;
            return e;
        }
        
        public static unsafe T AsEnum<T>(this ulong x) where T : unmanaged, Enum
        {
            var e = default(T);
            *(ulong*)&e = x;
            return e;
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
    }
}