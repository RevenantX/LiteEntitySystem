using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public unsafe delegate void MethodCallDelegate(void* ent, void* previousValue);
    
    //public for AOT
    public static class MethodCallGenerator
    {
        public static unsafe MethodCallDelegate Generate<TEnt, TValue>(MethodInfo method)
        {
            var d = (Action<TEnt, TValue>)method.CreateDelegate(typeof(Action<TEnt, TValue>));
            return (ent, previousValue) => d(Unsafe.AsRef<TEnt>(ent), Unsafe.Read<TValue>(previousValue));
        }

        internal static MethodInfo GetGenericMethod(Type entityType, Type valueType)
        {
            return typeof(MethodCallGenerator)
                .GetMethod(nameof(Generate))
                !.MakeGenericMethod(entityType, valueType);
        }
    }
}