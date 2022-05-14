using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public unsafe delegate void MethodCallDelegate(void* classPtr, void* value);

    //public for AOT
    public static class MethodCallGenerator
    {
        private static readonly Type SelfType = typeof(MethodCallGenerator);
        
        public static unsafe MethodCallDelegate Generate<TClass, TValue>(MethodInfo method)
        {
            var d = (Action<TClass, TValue>)method.CreateDelegate(typeof(Action<TClass, TValue>));
            return (classPtr, value) => d(Unsafe.AsRef<TClass>(classPtr), Unsafe.Read<TValue>(value));
        }
        
        public static unsafe MethodCallDelegate GenerateNoParams<TClass>(MethodInfo method)
        {
            var d = (Action<TClass>)method.CreateDelegate(typeof(Action<TClass>));
            return (classPtr, _) => d(Unsafe.AsRef<TClass>(classPtr));
        }

        internal static MethodInfo GetGenericMethod(Type entityType, Type valueType)
        {
            return valueType == null 
                ? SelfType.GetMethod(nameof(GenerateNoParams))!.MakeGenericMethod(entityType) 
                : SelfType.GetMethod(nameof(Generate))!.MakeGenericMethod(entityType, valueType);
        }
    }
}