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
    }
}