using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteEntitySystem.Internal
{
    internal static class MethodCallGenerator
    {
        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method, bool isSpan) where TValue : unmanaged
        {
            if (isSpan)
            {
                var d = (ArrayBinding<TClass, TValue>)method.CreateDelegate(typeof(ArrayBinding<TClass, TValue>));
                return (classPtr, buffer) => d((TClass)classPtr, MemoryMarshal.Cast<byte, TValue>(buffer));
            }
            else
            {
                var d = (Action<TClass, TValue>)method.CreateDelegate(typeof(Action<TClass, TValue>));
                return (classPtr, buffer) =>
                {
                    fixed(byte* data = buffer)
                        d((TClass)classPtr, *(TValue*)data);
                };
            }
      
        }

        public static MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method) 
        {
            var d = (Action<TClass>)method.CreateDelegate(typeof(Action<TClass>));
            return (classPtr, _) => d((TClass)classPtr);
        }
    }
}