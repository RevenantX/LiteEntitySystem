using System;
using K4os.Compression.LZ4;
using LiteNetLib.Utils;

namespace LiteEntitySystem.Extensions
{
    public class SyncNetSerializable<T> : SyncableField where T : INetSerializable
    {
        private static readonly NetDataWriter WriterCache = new();
        private static readonly NetDataReader ReaderCache = new();
        private static byte[] CompressionBuffer;
        
        private T _value;

        public T Value
        {
            get => _value;
            set
            {
                _value = value;
                OnSyncRequested();
            }
        }

        private static RemoteCallSpan<byte> _initAction;

        private readonly Func<T> _constructor;

        public SyncNetSerializable(Func<T> constructor)
        {
            _constructor = constructor;
        }

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected internal override void OnSyncRequested()
        {
            WriterCache.Reset();
            _value.Serialize(WriterCache);
            if (WriterCache.Length > ushort.MaxValue)
            {
                Logger.LogError("Too much sync data!");
                return;
            }
            int bufSize = LZ4Codec.MaximumOutputSize(WriterCache.Length) + 2;
            if(CompressionBuffer == null || CompressionBuffer.Length < bufSize)
                CompressionBuffer = new byte[bufSize];
            FastBitConverter.GetBytes(CompressionBuffer, 0, (ushort)WriterCache.Length);
            int encodedLength = LZ4Codec.Encode(
                WriterCache.Data,
                0,
                WriterCache.Length,
                CompressionBuffer,
                2,
                CompressionBuffer.Length-2,
                LZ4Level.L00_FAST);
            ExecuteRPC(_initAction, new ReadOnlySpan<byte>(CompressionBuffer, 0, encodedLength+2));
        }

        private void Init(ReadOnlySpan<byte> data)
        {
            ushort origSize = BitConverter.ToUInt16(data);
            if (CompressionBuffer == null || CompressionBuffer.Length < origSize)
                CompressionBuffer = new byte[origSize];
            LZ4Codec.Decode(data[2..], new Span<byte>(CompressionBuffer));
            ReaderCache.SetSource(CompressionBuffer, 0, origSize);
            _value ??= _constructor();
            _value.Deserialize(ReaderCache);
        }

        public static implicit operator T(SyncNetSerializable<T> field)
        {
            return field._value;
        }
    }
}