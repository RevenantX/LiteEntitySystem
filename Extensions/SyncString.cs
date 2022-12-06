using System;
using System.Runtime.CompilerServices;
using System.Text;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Extensions
{
    public class SyncString : SyncableField
    {
        private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);
        private byte[] _stringData;
        private string _string;
        private int _size;

        private RemoteCallSpan<byte> _setStringClientCall;

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
                ExecuteRPC(_setStringClientCall, _stringData);
            }
        }

        public override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetNewString, ref _setStringClientCall);
        }

        public override string ToString()
        {
            return _string;
        }

        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }
        
        private void SetNewString(ReadOnlySpan<byte> data)
        {
            _string = Encoding.GetString(data);
        }

        public override unsafe void FullSyncRead(ReadOnlySpan<byte> dataSpan, ref int position)
        {
            fixed (byte* data = dataSpan)
            {
                int length = *(ushort*)(data + position);
                Utils.ResizeOrCreate(ref _stringData, length);
                _string = Encoding.GetString(data + position + sizeof(ushort), length);
                position += sizeof(ushort) + length;
            }
        }

        public override unsafe void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            fixed (byte* data = dataSpan, stringData = _stringData)
            {
                *(ushort*)(data + position) = (ushort)_size;
                Unsafe.CopyBlock(data + position + sizeof(ushort), stringData, (uint)_size);
            }
            position += sizeof(ushort) + _size;
        }
    }
}