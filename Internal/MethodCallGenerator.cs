using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteEntitySystem.Internal
{
    public delegate void MethodCallDelegate(object classPtr, byte[] buffer, int offset, ushort count);

    //public for AOT
    public static class MethodCallGenerator
    {
        private static readonly Type SelfType = typeof(MethodCallGenerator);
        internal static readonly MethodInfo GenerateArrayMethod = SelfType.GetMethod(nameof(GenerateArray));
        internal static readonly MethodInfo GenerateMethod = SelfType.GetMethod(nameof(Generate));
        internal static readonly MethodInfo GenerateNoParamsMethod = SelfType.GetMethod(nameof(GenerateNoParams));
        
        private delegate void ArrayBinding<TClass, TValue>(TClass obj, ReadOnlySpan<TValue> arr);

        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d = (Action<TClass, TValue>)method.CreateDelegate(typeof(Action<TClass, TValue>));
            return (classPtr, buffer, offset, _) =>
            {
                fixed(byte* data = buffer)
                    d((TClass)classPtr, *(TValue*)(data+offset));
            };
        }

        public static MethodCallDelegate GenerateArray<TClass, TValue>(MethodInfo method) where TValue : unmanaged
        {
            var d = (ArrayBinding<TClass, TValue>)method.CreateDelegate(typeof(ArrayBinding<TClass, TValue>));
            return (classPtr, buffer, offset, count) => d((TClass)classPtr, MemoryMarshal.Cast<byte, TValue>(new ReadOnlySpan<byte>(buffer, offset, count)));
        }

        public static MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method) 
        {
            var d = (Action<TClass>)method.CreateDelegate(typeof(Action<TClass>));
            return (classPtr, _, _, _) => d((TClass)classPtr);
        }
    }
}