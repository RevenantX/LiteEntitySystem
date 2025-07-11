using System;
using LiteEntitySystem.Collections;

namespace LiteEntitySystem.Internal
{
    internal struct DeltaCompressor
    {
        private const int FieldsDivision = 2;
        
        //used for decode
        private byte[] _firstFullData;
        
        internal readonly int Size;
        internal readonly int MaxDeltaSize;
        internal readonly int DeltaBits;
        internal readonly int MinDeltaSize;

        public DeltaCompressor(int size)
        {
            Size = size;
            DeltaBits = (Size + FieldsDivision - 1) / FieldsDivision;
            MinDeltaSize = (DeltaBits + 7) / 8;
            MaxDeltaSize = MinDeltaSize + Size;
            _firstFullData = null;
        }

        public void Init()
        {
            if (_firstFullData == null)
                _firstFullData = new byte[Size];
            else
                Array.Clear(_firstFullData, 0, Size);
        }
        
        internal int Decode(ReadOnlySpan<byte> currentDeltaInput, Span<byte> result)
        {
            var deltaFlags = new BitReadOnlySpan(currentDeltaInput, DeltaBits);
            int fieldOffset = MinDeltaSize;
            for (int i = 0; i < Size; i += FieldsDivision)
            {
                if (deltaFlags[i / 2])
                {
                    _firstFullData[i] = result[i] = currentDeltaInput[fieldOffset];
                    if (i < Size - 1)
                        _firstFullData[i+1] = result[i+1] = currentDeltaInput[fieldOffset+1];
                    fieldOffset += FieldsDivision;
                }
                else
                {
                    result[i] = _firstFullData[i];
                    if(i < Size - 1)
                        result[i+1] = _firstFullData[i+1];
                }
            }
            return fieldOffset;
        }

        //when encode first data - use zeroes as previous data
        internal int Encode<T>(ref T nextData, Span<byte> result) where T : unmanaged
        {
            T prevData = default;
            return Encode(ref prevData, ref nextData, result);
        }

        internal unsafe int Encode<T>(ref T prevData, ref T nextData, Span<byte> result) where T : unmanaged
        {
            var deltaFlags = new BitSpan(result, DeltaBits);
            deltaFlags.Clear();
            int resultSize = MinDeltaSize;
            
            fixed (void* ptr1 = &prevData, ptr2 = &nextData)
            {
                byte* prevDataByte = (byte*)ptr1;
                byte* nextDataByte = (byte*)ptr2;
                for (int i = 0; i < Size; i += FieldsDivision)
                {
                    if (prevDataByte[i] != nextDataByte[i] || (i < Size - 1 && prevDataByte[i + 1] != nextDataByte[i + 1]))
                    {
                        deltaFlags[i / FieldsDivision] = true;
                        result[resultSize] = nextDataByte[i];
                        if(i < Size - 1)
                            result[resultSize + 1] = nextDataByte[i + 1];
                        resultSize += FieldsDivision;
                    }
                }
            }

            return resultSize;
        }
    }
}