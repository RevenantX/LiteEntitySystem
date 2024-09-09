using System;
using System.Runtime.InteropServices;
using System.Text;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public ref struct SpanWriter
    {
        public readonly Span<byte> RawData;
        public int Position;

        public SpanWriter(Span<byte> rawData)
        {
            RawData = rawData;
            Position = 0;
        }

        public void Put(float value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 4;
        }

        public void Put(double value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 8;
        }

        public void Put(long value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 8;
        }

        public void Put(ulong value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 8;
        }

        public void Put(int value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 4;
        }

        public void Put(uint value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 4;
        }

        public void Put(char value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 2;
        }

        public void Put(ushort value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 2;
        }

        public void Put(short value)
        {
            BitConverter.TryWriteBytes(RawData.Slice(Position), value);
            Position += 2;
        }

        public void Put(sbyte value)
        {
            RawData[Position] = (byte)value;
            Position++;
        }

        public void Put(byte value)
        {
            RawData[Position] = value;
            Position++;
        }

        public void Put(Guid value)
        {
            value.TryWriteBytes(RawData.Slice(Position));
            Position += 16;
        }

        public void Put(byte[] data, int offset, int length)
        {
            new ReadOnlySpan<byte>(data, offset, length).CopyTo(RawData.Slice(Position, length));
            Position += length;
        }

        public void Put(byte[] data)
        {
            new ReadOnlySpan<byte>(data).CopyTo(RawData.Slice(Position, data.Length));
            Position += data.Length;
        }

        public void PutSBytesWithLength(sbyte[] data, int offset, ushort length)
        {
            Put(length);
            MemoryMarshal.AsBytes(new ReadOnlySpan<sbyte>(data, offset, length)).CopyTo(RawData.Slice(Position + 2, length));
            Position += 2 + length;
        }
        
        public void PutBytesWithLength(byte[] data, int offset, ushort length)
        {
            Put(length);
            new ReadOnlySpan<byte>(data, offset, length).CopyTo(RawData.Slice(Position + 2, length));
            Position += 2 + length;
        }
        
        public void PutArray<T>(T[] arr, int sz) where T : unmanaged
        {
            ushort length = arr == null ? (ushort) 0 : (ushort)arr.Length;
            sz *= length;
            Put(length);
            if(arr != null)
                MemoryMarshal.AsBytes(new ReadOnlySpan<T>(arr)).CopyTo(RawData.Slice(Position+2,sz));
            Position += sz + 2;
        }
        
        public void PutBytesWithLength(byte[] data) => PutArray(data, 1);
        public void PutSBytesWithLength(sbyte[] data) => PutArray(data, 1);
        public void PutArray(float[] value) => PutArray(value, 4);
        public void PutArray(double[] value) => PutArray(value, 8);
        public void PutArray(long[] value) => PutArray(value, 8);
        public void PutArray(ulong[] value) => PutArray(value, 8);
        public void PutArray(int[] value) => PutArray(value, 4);
        public void PutArray(uint[] value) => PutArray(value, 4);
        public void PutArray(ushort[] value) => PutArray(value, 2);
        public void PutArray(short[] value) => PutArray(value, 2);
        public void PutArray(bool[] value) => PutArray(value, 1);
        public void Put(string value) => Put(value, 0);
        public void Put(bool value) => Put((byte)(value ? 1 : 0));
        public void Put<T>(T obj) where T : ISpanSerializable => obj.Serialize(this);
        
        public void PutArray(string[] value)
        {
            ushort strArrayLength = value == null ? (ushort)0 : (ushort)value.Length;
            Put(strArrayLength);
            for (int i = 0; i < strArrayLength; i++)
                Put(value[i]);
        }

        public void PutArray(string[] value, int strMaxLength)
        {
            ushort strArrayLength = value == null ? (ushort)0 : (ushort)value.Length;
            Put(strArrayLength);
            for (int i = 0; i < strArrayLength; i++)
                Put(value[i], strMaxLength);
        }

        public void PutArray<T>(T[] value) where T : ISpanSerializable, new()
        {
            ushort strArrayLength = (ushort)(value?.Length ?? 0);
            Put(strArrayLength);
            for (int i = 0; i < strArrayLength; i++)
                value[i].Serialize(this);
        }

        public void PutLargeString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Put(0);
                return;
            }
            int size = Utils.Encoding.Value.GetByteCount(value);
            if (size == 0)
            {
                Put(0);
                return;
            }
            Put(size);
            Utils.Encoding.Value.GetBytes(value, RawData.Slice(Position, size));
            Position += size;
        }

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        public void Put(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                Put((ushort)0);
                return;
            }
            
            int size = Utils.Encoding.Value.GetBytes(value, RawData.Slice(Position + sizeof(ushort)));
            if (size == 0)
            {
                Put((ushort)0);
                return;
            }
            Put(checked((ushort)(size + 1)));
            Position += size;
        }
    }
}
