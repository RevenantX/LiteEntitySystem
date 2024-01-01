using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public ref struct DeltaFieldsData
    {
        public bool FullSync;
        public Span<byte> RawData;
        public ReadOnlySpan<byte> FieldsBits;
        public Span<byte> PredictedData;
        public Span<byte> InterpolatedData;
        public bool WriteInterpolationData;
        public int ReaderPosition;
        public int InitialLength;

        internal ClientEntityManager.SyncCallInfo[] SyncCallInfos;
        internal int SyncCallsCount;

        private int _fieldCounter;

        public bool ContainsField()
        {
            bool contains = FullSync || Helpers.IsBitSet(FieldsBits, _fieldCounter);
            _fieldCounter++;
            return contains;
        }

        public void BindOnChange(SpanAction<InternalEntity, byte> onChangeAction, InternalEntity entity, int prevDataPos)
        {
            SyncCallInfos[SyncCallsCount++] = new ClientEntityManager.SyncCallInfo(onChangeAction, entity, prevDataPos);
        }
    }

    public ref struct WriteFieldsData
    {
        public Span<byte> Data;
        internal ushort[] FieldChangedTicks;
        internal ushort ServerTick;
        internal ushort MinimalTick;
        private int _fieldCounter;

        public void MarkChanged()
        {
            FieldChangedTicks[_fieldCounter++] = ServerTick;
        }
        
        public void MarkUnchanged()
        {
            if (Helpers.SequenceDiff(MinimalTick, FieldChangedTicks[_fieldCounter]) > 0)
                FieldChangedTicks[_fieldCounter] = MinimalTick;
            _fieldCounter++;
        }
    }
    
    public abstract class FieldManipulator
    {
        public virtual int DumpInterpolated(Span<byte> data) => 0;
        public virtual int LoadInterpolated(ReadOnlySpan<byte> data) => 0;
        public virtual int Interpolate(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer) => 0;
        public virtual void PreloadInterpolation(ref PreloadInterpolationData preloadData) { }
        public virtual int DumpLagCompensated(Span<byte> data) => 0;
        public virtual int LoadLagCompensated(ReadOnlySpan<byte> data) => 0;
        public virtual int ApplyLagCompensation(Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime) => 0;
        public virtual int LoadPredicted(ReadOnlySpan<byte> data) => 0;
        public virtual void ReadChanged(ref DeltaFieldsData fieldsData) { }
        public virtual void WriteChanged(ref WriteFieldsData fieldsData) { }
        public virtual void MakeDiff(ref MakeDiffData makeDiffData) { }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void InterpolateStruct<T>(ref ReadOnlySpan<byte> prev, ref ReadOnlySpan<byte> next, float fTimer, out T result) where T : unmanaged
        {
            if(ValueTypeProcessor<T>.InterpDelegate == null) 
                throw new Exception($"This type: {typeof(T)} can't be interpolated");
            fixed (byte* prevData = prev, nextData = next)
            {
                result = ValueTypeProcessor<T>.InterpDelegate(*(T*)prevData,*(T*)nextData,fTimer);
            }
            prev = prev.Slice(sizeof(T));
            next = next.Slice(sizeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void SliceBySize<T>(ref Span<byte> data) where T : unmanaged
        {
            data = data.Slice(sizeof(T));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void SliceBySize<T>(ref ReadOnlySpan<byte> data) where T : unmanaged
        {
            data = data.Slice(sizeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void WriteStruct<T>(ref Span<byte> data, T value) where T : unmanaged
        {
            fixed (byte* rawData = data)
                *(T*)rawData = value;
            data = data.Slice(sizeof(T));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void ReadStruct<T>(ref ReadOnlySpan<byte> data, out T result) where T : unmanaged
        {
            fixed (byte* rawData = data)
                result = *(T*)rawData;
            data = data.Slice(sizeof(T));
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static unsafe void ReadStruct<T>(ref Span<byte> data, out T result) where T : unmanaged
        {
            fixed (byte* rawData = data)
                result = *(T*)rawData;
            data = data.Slice(sizeof(T));
        }
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