using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public static class Helpers
    {
        private static class EnumInfo<T> where T : unmanaged, Enum
        {
            public static readonly int Size = Unsafe.SizeOf<T>();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool HasFlagFast<T>(this T e, T flag) where T : unmanaged, Enum
        {
            switch (EnumInfo<T>.Size)
            {
                case 1:
                    return (Unsafe.Read<byte>(&e) & Unsafe.Read<byte>(&flag)) != 0;
                case 2:
                    return (Unsafe.Read<short>(&e) & Unsafe.Read<short>(&flag)) != 0;
                case 4:
                    return (Unsafe.Read<int>(&e) & Unsafe.Read<int>(&flag)) != 0;
                case 8:
                    return (Unsafe.Read<long>(&e) & Unsafe.Read<long>(&flag)) != 0;
            }
            return false;
        }
    }
}