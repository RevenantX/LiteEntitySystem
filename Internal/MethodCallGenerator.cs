using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public unsafe delegate void MethodCallDelegate(void* classPtr, void* value, ushort count);

    //public for AOT
    public static class MethodCallGenerator
    {
        private static readonly Type SelfType = typeof(MethodCallGenerator);
        private static readonly MethodInfo GenerateMethod = SelfType.GetMethod(nameof(Generate));
        private static readonly MethodInfo GenerateNoParamsMethod = SelfType.GetMethod(nameof(GenerateNoParams));
        private static readonly MethodInfo GenerateArrayMethod = SelfType.GetMethod(nameof(GenerateArray));
        
        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method)
        {
            var d = (Action<TClass, TValue>)method.CreateDelegate(typeof(Action<TClass, TValue>));
            return (classPtr, value, _) => d(Unsafe.AsRef<TClass>(classPtr), Unsafe.Read<TValue>(value));
        }
        
        public static unsafe MethodCallDelegate GenerateArray<TClass, TValue>(MethodInfo method)
        {
            var d = (Action<TClass, TValue, ushort>)method.CreateDelegate(typeof(Action<TClass, TValue, ushort>));
            return (classPtr, value, count) => d(Unsafe.AsRef<TClass>(classPtr), Unsafe.Read<TValue>(value), count);
        }
        
        public static unsafe MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method)
        {
            var d = (Action<TClass>)method.CreateDelegate(typeof(Action<TClass>));
            return (classPtr, _, _) => d(Unsafe.AsRef<TClass>(classPtr));
        }

        internal static MethodInfo GetGenericMethod(Type entityType, Type valueType)
        {
            return valueType == null 
                ? GenerateNoParamsMethod.MakeGenericMethod(entityType) 
                : valueType.IsArray 
                    ? GenerateArrayMethod.MakeGenericMethod(entityType, valueType)
                    : GenerateMethod.MakeGenericMethod(entityType, valueType);
        }
    }
}