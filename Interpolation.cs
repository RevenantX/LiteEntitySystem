using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    internal unsafe delegate void InterpolatorDelegate(byte* prev, byte* current, byte* result, float t);
    
    public static class Interpolation
    {
        public delegate void InterpolatorDelegate<T>(T prev, T current, out T result, float t) where T : struct;
        public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : struct;
        
        internal static readonly Dictionary<Type, InterpolatorDelegate> Methods = new Dictionary<Type, InterpolatorDelegate>();

        public static unsafe void Register<T>(InterpolatorDelegate<T> interpolator) where T : struct
        {
            Methods[typeof(T)] = (a, b, result, t) =>
            {
                interpolator(
                    Unsafe.AsRef<T>(a),
                    Unsafe.AsRef<T>(b),
                    out Unsafe.AsRef<T>(result),
                    t);
            };
        }
        
        public static unsafe void Register<T>(InterpolatorDelegateWithReturn<T> interpolator) where T : struct
        {
            Methods[typeof(T)] = (a, b, result, t) =>
            {
                Unsafe.AsRef<T>(result) = interpolator(
                    Unsafe.AsRef<T>(a),
                    Unsafe.AsRef<T>(b),
                    t);
            };
        }
    }
}