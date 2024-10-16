namespace LiteEntitySystem
{
    public interface ISpanSerializable
    {
        int MaxSize { get; }
        void Serialize(ref SpanWriter writer);
        void Deserialize(ref SpanReader reader);
    }
}