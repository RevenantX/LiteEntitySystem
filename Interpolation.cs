using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    internal unsafe delegate void InterpolatorDelegate(byte* prev, byte* current, byte* result, float t);
    
    /// <summary>
    /// Class for registering interpolation methods for different types
    /// </summary>
    public static class Interpolation
    {
        public delegate void InterpolatorDelegate<T>(T prev, T current, out T result, float t) where T : struct;
        public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : struct;
        
        internal static readonly Dictionary<Type, InterpolatorDelegate> Methods = new Dictionary<Type, InterpolatorDelegate>();

        /// <summary>
        /// Register interpolation method for type
        /// </summary>
        /// <param name="interpolator">interpolation method</param>
        /// <typeparam name="T">Type of interpolated value</typeparam>
        public static unsafe void Register<T>(InterpolatorDelegate<T> interpolator) where T : struct
        {
            Methods[typeof(T)] = (a, b, result, t) =>
            {
                interpolator(
                    Unsafe.Read<T>(a),
                    Unsafe.Read<T>(b),
                    out Unsafe.AsRef<T>(result),
                    t);
            };
        }
        
        /// <summary>
        /// Register interpolation method for type
        /// </summary>
        /// <param name="interpolator">interpolation method (eg Vector3.Lerp)</param>
        /// <typeparam name="T">Type of interpolated value</typeparam>
        public static unsafe void Register<T>(InterpolatorDelegateWithReturn<T> interpolator) where T : struct
        {
            Methods[typeof(T)] = (a, b, result, t) =>
            {
                Unsafe.AsRef<T>(result) = interpolator(
                    Unsafe.Read<T>(a),
                    Unsafe.Read<T>(b),
                    t);
            };
        }
    }
}