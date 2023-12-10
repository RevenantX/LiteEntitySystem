namespace LiteEntitySystem.Internal
{
    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;

    public class ValueTypeProcessor<T> where T : unmanaged
    {
        public static InterpolatorDelegateWithReturn<T> InterpDelegate { get; private set; }
        
        public ValueTypeProcessor(InterpolatorDelegateWithReturn<T> interpDelegate) 
        {
            InterpDelegate ??= interpDelegate;
        }
    }
}