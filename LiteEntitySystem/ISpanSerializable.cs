namespace LiteEntitySystem
{
    public interface ISpanSerializable
    {
        int MaxSize { get; }
        void Serialize(SpanWriter writer);
        void Deserialize(SpanReader reader);
    }
}