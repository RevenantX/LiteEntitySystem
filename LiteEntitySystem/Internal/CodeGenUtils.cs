using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public static class CodeGenUtils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FieldSave(SyncableField s, in EntityFieldInfo field, Span<byte> result)
        {
            s.FieldSave(in field, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FieldLoad(SyncableField s, in EntityFieldInfo field, ReadOnlySpan<byte> data)
        {
            s.FieldLoad(in field, data);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FieldSaveIfDifferent(SyncableField s, in EntityFieldInfo field, Span<byte> result)
        {
            return s.FieldSaveIfDifferent(in field, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FieldLoadIfDifferent(SyncableField s, in EntityFieldInfo field, ReadOnlySpan<byte> data)
        {
            return s.FieldLoadIfDifferent(in field, data);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FieldSetInterpolation(SyncableField s, in EntityFieldInfo field, ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer)
        {
            s.FieldSetInterpolation(in field, prev, current, fTimer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FieldLoadHistory(SyncableField s, in EntityFieldInfo field, Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime)
        {
            s.FieldLoadHistory(in field, tempHistory, historyA, historyB, lerpTime);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FieldOnChange(SyncableField s, in EntityFieldInfo field, ReadOnlySpan<byte> prevData)
        {
            s.FieldOnChange(in field, prevData);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void OnSyncRequested(SyncableField s)
        {
            s.OnSyncRequested();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterRPC(SyncableField s, InternalEntity entity)
        {
            s.ParentEntityId = entity.Id;
            s.RegisterRPC(new SyncableRPCRegistrator(entity));
        }

        public static void InternalSyncablesSetId(SyncableField s, ushort parentId)
        {
            s.ParentEntityId = parentId;
        }
    }
}