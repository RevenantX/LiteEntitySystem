using System.Runtime.CompilerServices;
using System.Text;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        public abstract unsafe void FullSyncWrite(byte* data, ref int position);
        public abstract unsafe void FullSyncRead(byte* data, ref int position);
    }
    
    public class SyncString : SyncableField
    {
        private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);
        private byte[] _stringData;
        private string _string;
        private int _size;
        
        public string Value
        {
            get => _string;
            set
            {
                if (_string == value)
                    return;
                _string = value;
                Utils.ResizeOrCreate(ref _stringData, Encoding.GetMaxByteCount(_string.Length));
                _size = Encoding.GetBytes(_string, 0, _string.Length, _stringData, 0);
            }
        }

        public override string ToString()
        {
            return _string;
        }

        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            int length = *(ushort*)(data + position);
            Utils.ResizeOrCreate(ref _stringData, length);
            _string = Encoding.GetString(data + position + sizeof(ushort), length);
            position += sizeof(ushort) + length;
        }

        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            *(ushort*)(data + position) = (ushort)_size;
            fixed (byte* stringData = _stringData)
            {
                Unsafe.CopyBlock(data + position + sizeof(ushort), stringData, (uint)_size);
            }
            position += sizeof(ushort) + _size;
        }
    }
}