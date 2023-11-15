using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;
    
    internal static class ValueProcessors
    {
        public static readonly Dictionary<Type, ValueTypeProcessor> RegisteredProcessors = new ();
    }
    
    public abstract class ValueTypeProcessor
    {
        internal readonly Type ValueType;
        internal readonly int Size;

        protected ValueTypeProcessor(Type valueType, int size)
        {
            ValueType = valueType;
            Size = size;
        }
    }

    public unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public static InterpolatorDelegateWithReturn<T> InterpDelegate { get; private set; }
        
        public ValueTypeProcessor(InterpolatorDelegateWithReturn<T> interpDelegate) : base(typeof(T), sizeof(T))
        {
            InterpDelegate ??= interpDelegate;
        }
    }
}