using System;

namespace LiteEntitySystem.Collections
{
    public readonly ref struct BitSpan
    {
        private readonly Span<byte> _bitRegion;
        private const int BitsInByte = 8;
        
        public readonly int BitCount;
        public readonly int ByteCount;
        
        public BitSpan(Span<byte> bitRegion)
        {
            BitCount = bitRegion.Length * BitsInByte;
            ByteCount = bitRegion.Length;
            _bitRegion = bitRegion;
        }
        
        public BitSpan(Span<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = bitRegion;
        }
        
        public unsafe BitSpan(byte* bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = new Span<byte>(bitRegion, ByteCount);
        }
        
        public BitSpan(byte[] bitRegion, int offset, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = new Span<byte>(bitRegion, offset, ByteCount);
        }

        public bool this[int index]
        {
            get => (_bitRegion[index / BitsInByte] & (byte)(1 << (index % BitsInByte))) != 0;
            set
            {
                if (value)
                    _bitRegion[index / BitsInByte] |= (byte)(1 << (index % BitsInByte));
                else
                    _bitRegion[index / BitsInByte] &= (byte)~(1 << (index % BitsInByte));
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
        private const int BitsInByte = 8;
        
        public readonly int BitCount;
        public readonly int ByteCount;
        
        public BitReadOnlySpan(Span<byte> bitRegion)
        {
            BitCount = bitRegion.Length * BitsInByte;
            ByteCount = bitRegion.Length;
            _bitRegion = bitRegion;
        }
        
        public BitReadOnlySpan(Span<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = bitRegion;
        }
        
        public BitReadOnlySpan(ReadOnlySpan<byte> bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = bitRegion;
        }
        
        public unsafe BitReadOnlySpan(byte* bitRegion, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = new Span<byte>(bitRegion, ByteCount);
        }
        
        public BitReadOnlySpan(byte[] bitRegion, int offset, int bitCount)
        {
            BitCount = bitCount;
            ByteCount = bitCount / BitsInByte + (bitCount % BitsInByte == 0 ? 0 : 1);
            _bitRegion = new Span<byte>(bitRegion, offset, ByteCount);
        }

        public bool this[int index] => (_bitRegion[index / BitsInByte] & (byte)(1 << (index % BitsInByte))) != 0;

        public override unsafe string ToString()
        {
            var chars = stackalloc char[BitCount + 1];
            for (int i = 0; i < BitCount; i++)
                chars[i] = this[i] ? '1' : '0';
            return new string(chars);
        }
    }
}