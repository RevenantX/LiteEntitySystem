using System;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public ref struct SpanReader
    {
        public readonly ReadOnlySpan<byte> RawData;
        public int Position;
        public int AvailableBytes => RawData.Length - Position;
        
        public SpanReader(ReadOnlySpan<byte> rawData)
        {
            RawData = rawData;
            Position = 0;
        }
        
        public void Get<T>(out T result) where T : struct, ISpanSerializable
        {
            result = default;
            result.Deserialize(this);
        }

        public void Get<T>(out T result, Func<T> constructor) where T : class, ISpanSerializable
        {
            result = constructor();
            result.Deserialize(this);
        }
        
        public void Get(out byte result) => result = GetByte();
        public void Get(out sbyte result) => result = (sbyte)GetByte();
        public void Get(out bool result) => result = GetBool();
        public void Get(out char result) => result = GetChar();
        public void Get(out ushort result) => result = GetUShort();
        public void Get(out short result) => result = GetShort();
        public void Get(out ulong result) => result = GetULong();
        public void Get(out long result) => result = GetLong();
        public void Get(out uint result) => result = GetUInt();
        public void Get(out int result) => result = GetInt();
        public void Get(out double result) => result = GetDouble();
        public void Get(out float result) => result = GetFloat();
        public void Get(out string result) => result = GetString();
        public void Get(out string result, int maxLength) => result = GetString(maxLength);
        public void Get(out Guid result) => result = GetGuid();
        public byte GetByte() => RawData[Position++];
        public sbyte GetSByte() => (sbyte)RawData[Position++];
        
        public T[] GetArray<T>(ushort size) where T : unmanaged
        {
            ushort length = BitConverter.ToUInt16(RawData.Slice(Position));
            Position += 2;
            var result = new T[length];
            length *= size;
            MemoryMarshal.Cast<byte, T>(RawData.Slice(Position, length)).CopyTo(new Span<T>(result));
            Position += length;
            return result;
        }

        public T[] GetArray<T>() where T : ISpanSerializable, new()
        {
            ushort length = BitConverter.ToUInt16(RawData.Slice(Position));
            Position += 2;
            var result = new T[length];
            for (int i = 0; i < length; i++)
            {
                var item = new T();
                item.Deserialize(this);
                result[i] = item;
            }
            return result;
        }
        
        public T[] GetArray<T>(Func<T> constructor) where T : class, ISpanSerializable
        {
            ushort length = BitConverter.ToUInt16(RawData.Slice(Position));
            Position += 2;
            var result = new T[length];
            for (int i = 0; i < length; i++)
                Get(out result[i], constructor);
            return result;
        }
        
        public bool[] GetBoolArray() => GetArray<bool>(1);
        public ushort[] GetUShortArray() => GetArray<ushort>(2);
        public short[] GetShortArray() => GetArray<short>(2);
        public int[] GetIntArray() => GetArray<int>(4);
        public uint[] GetUIntArray() => GetArray<uint>(4);
        public float[] GetFloatArray() => GetArray<float>(4);
        public double[] GetDoubleArray() => GetArray<double>(8);
        public long[] GetLongArray() => GetArray<long>(8);
        public ulong[] GetULongArray() => GetArray<ulong>(8);
        
        public string[] GetStringArray()
        {
            ushort length = GetUShort();
            string[] arr = new string[length];
            for (int i = 0; i < length; i++)
            {
                arr[i] = GetString();
            }
            return arr;
        }

        /// <summary>
        /// Note that "maxStringLength" only limits the number of characters in a string, not its size in bytes.
        /// Strings that exceed this parameter are returned as empty
        /// </summary>
        public string[] GetStringArray(int maxStringLength)
        {
            ushort length = GetUShort();
            string[] arr = new string[length];
            for (int i = 0; i < length; i++)
            {
                arr[i] = GetString(maxStringLength);
            }
            return arr;
        }

        public bool GetBool() => GetByte() == 1;
        public char GetChar() => (char)GetUShort();

        public ushort GetUShort()
        {
            ushort result = BitConverter.ToUInt16(RawData.Slice(Position));
            Position += 2;
            return result;
        }

        public short GetShort()
        {
            short result = BitConverter.ToInt16(RawData.Slice(Position));
            Position += 2;
            return result;
        }

        public long GetLong()
        {
            long result = BitConverter.ToInt64(RawData.Slice(Position));
            Position += 8;
            return result;
        }

        public ulong GetULong()
        {
            ulong result = BitConverter.ToUInt64(RawData.Slice(Position));
            Position += 8;
            return result;
        }

        public int GetInt()
        {
            int result = BitConverter.ToInt32(RawData.Slice(Position));
            Position += 4;
            return result;
        }

        public uint GetUInt()
        {
            uint result = BitConverter.ToUInt32(RawData.Slice(Position));
            Position += 4;
            return result;
        }

        public float GetFloat()
        {
            float result = BitConverter.ToSingle(RawData.Slice(Position));
            Position += 4;
            return result;
        }

        public double GetDouble()
        {
            double result = BitConverter.ToDouble(RawData.Slice(Position));
            Position += 8;
            return result;
        }

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        /// <returns>"string.Empty" if value > "maxLength"</returns>
        public string GetString(int maxLength)
        {
            ushort size = GetUShort();
            if (size == 0)
                return string.Empty;
            
            int actualSize = size - 1;
            string result = maxLength > 0 && Utils.Encoding.Value.GetCharCount(RawData.Slice(Position, actualSize)) > maxLength ?
                string.Empty :
                Utils.Encoding.Value.GetString(RawData.Slice(Position, actualSize));
            Position += actualSize;
            return result;
        }

        public string GetString()
        {
            ushort size = GetUShort();
            if (size == 0)
                return string.Empty;
            
            int actualSize = size - 1;
            string result = Utils.Encoding.Value.GetString(RawData.Slice(Position, actualSize));
            Position += actualSize;
            return result;
        }

        public string GetLargeString()
        {
            int size = GetInt();
            if (size <= 0)
                return string.Empty;
            string result = Utils.Encoding.Value.GetString(RawData.Slice(Position, size));
            Position += size;
            return result;
        }
        
        public Guid GetGuid()
        {
            var result = new Guid(RawData.Slice(Position, 16));
            Position += 16;
            return result;
        }

        public T Get<T>() where T : struct, ISpanSerializable
        {
            var obj = default(T);
            obj.Deserialize(this);
            return obj;
        }

        public T Get<T>(Func<T> constructor) where T : class, ISpanSerializable
        {
            var obj = constructor();
            obj.Deserialize(this);
            return obj;
        }
        
        public ReadOnlySpan<byte> GetRemainingBytesSpan() => RawData.Slice(Position);

        public byte[] GetRemainingBytes()
        {
            byte[] outgoingData = new byte[AvailableBytes];
            RawData.Slice(Position, AvailableBytes).CopyTo(new Span<byte>(outgoingData, 0, AvailableBytes));
            Position = RawData.Length;
            return outgoingData;
        }

        public void GetBytes(byte[] destination, int start, int count)
        {
            RawData.Slice(Position, count).CopyTo(new Span<byte>(destination, start, count));
            Position += count;
        }

        public void GetBytes(byte[] destination, int count)
        {
            RawData.Slice(Position, count).CopyTo(new Span<byte>(destination, 0, count));
            Position += count;
        }

        public sbyte[] GetSBytesWithLength() => GetArray<sbyte>(1);
        public byte[] GetBytesWithLength() => GetArray<byte>(1);
        public byte PeekByte() => RawData[Position];
        public sbyte PeekSByte() => (sbyte)RawData[Position];
        public bool PeekBool() => RawData[Position] == 1;
        public char PeekChar() => (char)PeekUShort();
        public ushort PeekUShort() => BitConverter.ToUInt16(RawData.Slice(Position));
        public short PeekShort() => BitConverter.ToInt16(RawData.Slice(Position));
        public long PeekLong() => BitConverter.ToInt64(RawData.Slice(Position));
        public ulong PeekULong() => BitConverter.ToUInt64(RawData.Slice(Position));
        public int PeekInt() => BitConverter.ToInt32(RawData.Slice(Position));
        public uint PeekUInt() => BitConverter.ToUInt32(RawData.Slice(Position));
        public float PeekFloat() => BitConverter.ToSingle(RawData.Slice(Position));
        public double PeekDouble() => BitConverter.ToDouble(RawData.Slice(Position));

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        public string PeekString(int maxLength)
        {
            ushort size = PeekUShort();
            if (size == 0)
                return string.Empty;
            
            int actualSize = size - 1;
            return maxLength > 0 && Utils.Encoding.Value.GetCharCount(RawData.Slice(Position + 2, actualSize)) > maxLength ?
                string.Empty :
                Utils.Encoding.Value.GetString(RawData.Slice(Position + 2, actualSize));
        }

        public string PeekString()
        {
            ushort size = PeekUShort();
            if (size == 0)
                return string.Empty;

            int actualSize = size - 1;
            return Utils.Encoding.Value.GetString(RawData.Slice(Position + 2, actualSize));
        }

        public bool TryGetByte(out byte result)
        {
            if (AvailableBytes >= 1)
            {
                result = GetByte();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetSByte(out sbyte result)
        {
            if (AvailableBytes >= 1)
            {
                result = GetSByte();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetBool(out bool result)
        {
            if (AvailableBytes >= 1)
            {
                result = GetBool();
                return true;
            }
            result = false;
            return false;
        }

        public bool TryGetChar(out char result)
        {
            if (!TryGetUShort(out ushort uShortValue))
            {
                result = '\0';
                return false;
            }
            result = (char)uShortValue;
            return true;
        }

        public bool TryGetShort(out short result)
        {
            if (AvailableBytes >= 2)
            {
                result = GetShort();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetUShort(out ushort result)
        {
            if (AvailableBytes >= 2)
            {
                result = GetUShort();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetInt(out int result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetInt();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetUInt(out uint result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetUInt();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetLong(out long result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetLong();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetULong(out ulong result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetULong();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetFloat(out float result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetFloat();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetDouble(out double result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetDouble();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetString(out string result)
        {
            if (AvailableBytes >= 2)
            {
                ushort strSize = PeekUShort();
                if (AvailableBytes >= strSize + 1)
                {
                    result = GetString();
                    return true;
                }
            }
            result = null;
            return false;
        }

        public bool TryGetStringArray(out string[] result)
        {
            if (!TryGetUShort(out ushort strArrayLength)) {
                result = null;
                return false;
            }

            result = new string[strArrayLength];
            for (int i = 0; i < strArrayLength; i++)
            {
                if (!TryGetString(out result[i]))
                {
                    result = null;
                    return false;
                }
            }

            return true;
        }

        public bool TryGetBytesWithLength(out byte[] result)
        {
            if (AvailableBytes >= 2)
            {
                ushort length = PeekUShort();
                if (length >= 0 && AvailableBytes >= 2 + length)
                {
                    result = GetBytesWithLength();
                    return true;
                }
            }
            result = null;
            return false;
        }
    }
}
