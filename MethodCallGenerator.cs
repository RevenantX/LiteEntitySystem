using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public unsafe delegate void MethodCallDelegate(void* ent, void* previousValue);

    public static class MethodCallGenerator
    {
        //public for AOT
        public static unsafe MethodCallDelegate Generate<TEnt, TValue>(MethodInfo method)
        {
            var typedDelegate = (Action<TEnt, TValue>)method.CreateDelegate(typeof(Action<TEnt, TValue>));
            return (ent, previousValue) =>
            {
                TValue prev = default(TValue);
                Unsafe.Copy(ref prev, previousValue);
                typedDelegate(Unsafe.AsRef<TEnt>(ent), prev);
            };
        }

        internal static MethodInfo GetGenericMethod(Type entityType, Type valueType)
        {
            return typeof(MethodCallGenerator)
                .GetMethod(nameof(Generate))
                !.MakeGenericMethod(entityType, valueType);
        }
    }
}