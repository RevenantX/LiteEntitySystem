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

        private Action<byte[], ushort> _setStringClientCall;

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
                _setStringClientCall?.Invoke(_stringData, (ushort)_size);
            }
        }

        public override void OnServerInitialized()
        {
            CreateClientAction(SetNewString, out _setStringClientCall);
        }

        public override string ToString()
        {
            return _string;
        }

        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }

        [SyncableRemoteCall]
        private void SetNewString(byte[] data, ushort count)
        {
            _string = Encoding.GetString(data, 0, count);
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            int length = Unsafe.Read<ushort>(data + position);
            Utils.ResizeOrCreate(ref _stringData, length);
            _string = Encoding.GetString(data + position + sizeof(ushort), length);
            position += sizeof(ushort) + length;
        }

        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            Unsafe.Write(data + position, (ushort)_size);
            fixed (byte* stringData = _stringData)
            {
                Unsafe.CopyBlock(data + position + sizeof(ushort), stringData, (uint)_size);
            }
            position += sizeof(ushort) + _size;
        }
    }
}