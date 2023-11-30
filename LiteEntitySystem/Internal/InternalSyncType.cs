using System;

namespace LiteEntitySystem.Internal
{
    public abstract class FieldManipulator
    {
        public virtual void Save(in EntityFieldInfo field, Span<byte> result) {}
        public virtual void Load(in EntityFieldInfo field, ReadOnlySpan<byte> data) {}
        public virtual bool SaveIfDifferent(in EntityFieldInfo field, Span<byte> result) { return false; }
        public virtual bool LoadIfDifferent(in EntityFieldInfo field, ReadOnlySpan<byte> data) { return false; }
        public virtual void SetInterpolation(in EntityFieldInfo field, ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer) {}
        public virtual void LoadHistory(in EntityFieldInfo field, Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime) {}
        public virtual void OnChange(in EntityFieldInfo field, ReadOnlySpan<byte> prevData) {}
    }
    
    public abstract class InternalSyncType
    {
        protected internal virtual FieldManipulator GetFieldManipulator()
        {
            return null;
        }

        protected internal virtual GeneratedClassMetadata GetClassMetadata()
        {
            return null;
        }
    }
}