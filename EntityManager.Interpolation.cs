using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    internal unsafe delegate void InterpolatorDelegate(byte* prev, byte* current, byte* result, float t);
    public delegate void InterpolatorDelegate<T>(T prev, T current, out T result, float t) where T : struct;

    public partial class EntityManager
    {
        internal static readonly Dictionary<Type, InterpolatorDelegate> InterpolatedData = new Dictionary<Type, InterpolatorDelegate>();

        public static unsafe void RegisterInterpolator<T>(InterpolatorDelegate<T> interpolator) where T : struct
        {
            InterpolatedData[typeof(T)] = (prev, current, result, f) =>
            {
                interpolator(
                    Unsafe.AsRef<T>(prev),
                    Unsafe.AsRef<T>(current),
                    out Unsafe.AsRef<T>(result),
                    f);
            };
        }
    }
}