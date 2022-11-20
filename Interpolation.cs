using System;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    internal unsafe delegate void InterpolatorDelegate(byte* prev, byte* current, byte* result, float t);
    
    /// <summary>
    /// Class for registering interpolation methods for different types
    /// </summary>
    public static class Interpolation
    {
        public delegate void InterpolatorDelegate<T>(T prev, T current, out T result, float t) where T : unmanaged;
        public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;
        
        internal static readonly Dictionary<Type, InterpolatorDelegate> Methods = new Dictionary<Type, InterpolatorDelegate>();

        /// <summary>
        /// Register interpolation method for type
        /// </summary>
        /// <param name="interpolator">interpolation method</param>
        /// <typeparam name="T">Type of interpolated value</typeparam>
        public static unsafe void Register<T>(InterpolatorDelegate<T> interpolator) where T : unmanaged
        {
            Methods[typeof(T)] = (a, b, result, t) => interpolator(*(T*)a, *(T*)b, out *(T*)result, t);
        }
        
        /// <summary>
        /// Register interpolation method for type
        /// </summary>
        /// <param name="interpolator">interpolation method (eg Vector3.Lerp)</param>
        /// <typeparam name="T">Type of interpolated value</typeparam>
        public static unsafe void Register<T>(InterpolatorDelegateWithReturn<T> interpolator) where T : unmanaged
        {
            Methods[typeof(T)] = (a, b, result, t) => *(T*)result = interpolator(*(T*)a, *(T*)b, t);
        }
    }
}