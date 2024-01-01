using System;

namespace LiteEntitySystem.Internal
{
    public readonly ref struct BitSpan
    {
        private readonly Span<byte> _bitRegion;
        
        public readonly int BitCount;
        public readonly int ByteCount;
        
        public BitSpan(Span<byte> bitRegion)
        {
            BitCount = bitRegion.Length * Helpers.BitsInByte;
            ByteCount = bitRegion.Length;
            _bitRegion = bitRegion;
        }
        
        public BitSpan(Span<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = bitRegion;
        }
        
        public unsafe BitSpan(byte* bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = new Span<byte>(bitRegion, ByteCount);
        }
        
        public BitSpan(byte[] bitRegion, int offset, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = new Span<byte>(bitRegion, offset, ByteCount);
        }

        public void SetBit(int index)
        {
            _bitRegion[index / Helpers.BitsInByte] |= (byte)(1 << (index % Helpers.BitsInByte));
        }

        public void ClearBit(int index)
        {
            _bitRegion[index / Helpers.BitsInByte] &= (byte)~(1 << (index % Helpers.BitsInByte));
        }

        public bool this[int index]
        {
            get => (_bitRegion[index / Helpers.BitsInByte] & (byte)(1 << (index % Helpers.BitsInByte))) != 0;
            set
            {
                if (value)
                    _bitRegion[index / Helpers.BitsInByte] |= (byte)(1 << (index % Helpers.BitsInByte));
                else
                    _bitRegion[index / Helpers.BitsInByte] &= (byte)~(1 << (index % Helpers.BitsInByte));
            }
        }

        public override unsafe string ToString()
        {
            var chars = stackalloc char[BitCount + 1];
            for (int i = 0; i < BitCount; i++)
                chars[i] = this[i] ? '1' : '0';
            return new string(chars);
        }

        public void Clear()
        {
            _bitRegion.Clear();
        }
    }
    
    public readonly ref struct BitReadOnlySpan
    {
        private readonly ReadOnlySpan<byte> _bitRegion;
        
        public readonly int BitCount;
        public readonly int ByteCount;
        
        public BitReadOnlySpan(Span<byte> bitRegion)
        {
            BitCount = bitRegion.Length * Helpers.BitsInByte;
            ByteCount = bitRegion.Length;
            _bitRegion = bitRegion;
        }
        
        public BitReadOnlySpan(Span<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = bitRegion;
        }
        
        public BitReadOnlySpan(ReadOnlySpan<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = bitRegion;
        }
        
        public unsafe BitReadOnlySpan(byte* bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = new Span<byte>(bitRegion, ByteCount);
        }
        
        public BitReadOnlySpan(byte[] bitRegion, int offset, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = (bitCount + Helpers.BitsInByteMinusOne) / Helpers.BitsInByte;
            _bitRegion = new Span<byte>(bitRegion, offset, ByteCount);
        }

        public bool this[int index] => (_bitRegion[index / Helpers.BitsInByte] & (byte)(1 << (index % Helpers.BitsInByte))) != 0;

        public override unsafe string ToString()
        {
            var chars = stackalloc char[BitCount + 1];
            for (int i = 0; i < BitCount; i++)
                chars[i] = this[i] ? '1' : '0';
            return new string(chars);
        }
    }
}