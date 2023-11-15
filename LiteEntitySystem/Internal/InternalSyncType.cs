using System;

namespace LiteEntitySystem.Internal
{
    public abstract class InternalSyncType
    {
        protected internal virtual void FieldSave(in EntityFieldInfo field, Span<byte> result) {}
        protected internal virtual void FieldLoad(in EntityFieldInfo field, ReadOnlySpan<byte> data) {}
        protected internal virtual bool FieldSaveIfDifferent(in EntityFieldInfo field, Span<byte> result) { return false; }
        protected internal virtual bool FieldLoadIfDifferent(in EntityFieldInfo field, ReadOnlySpan<byte> data) { return false; }
        protected internal virtual void FieldSetInterpolation(in EntityFieldInfo field, ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer) {}
        protected internal virtual void FieldLoadHistory(in EntityFieldInfo field, Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime) {}
        protected internal virtual void FieldOnChange(in EntityFieldInfo field, ReadOnlySpan<byte> prevData) {}
    }
}