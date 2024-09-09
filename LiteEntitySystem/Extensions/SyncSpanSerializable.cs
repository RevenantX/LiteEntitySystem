using System;
using K4os.Compression.LZ4;
using LiteNetLib.Utils;

namespace LiteEntitySystem.Extensions
{
    public class SyncSpanSerializable<T> : SyncableField where T : ISpanSerializable
    {
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

        public SyncSpanSerializable(Func<T> constructor)
        {
            _constructor = constructor;
        }

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected internal override unsafe void OnSyncRequested()
        {
            var spanWriter = new SpanWriter(stackalloc byte[_value.MaxSize]);
            _value.Serialize(spanWriter);
            if (spanWriter.Position > ushort.MaxValue)
            {
                Logger.LogError("Too much sync data!");
                return;
            }
            int bufSize = LZ4Codec.MaximumOutputSize(spanWriter.Position) + 2;
            if(CompressionBuffer == null || CompressionBuffer.Length < bufSize)
                CompressionBuffer = new byte[bufSize];
            FastBitConverter.GetBytes(CompressionBuffer, 0, (ushort)spanWriter.Position);
            int encodedLength = LZ4Codec.Encode(
                spanWriter.RawData.Slice(0, spanWriter.Position),
                new Span<byte>(CompressionBuffer, 2, CompressionBuffer.Length-2),
                LZ4Level.L00_FAST);
            ExecuteRPC(_initAction, new ReadOnlySpan<byte>(CompressionBuffer, 0, encodedLength+2));
        }

        private void Init(ReadOnlySpan<byte> data)
        {
            ushort origSize = BitConverter.ToUInt16(data);
            if (CompressionBuffer == null || CompressionBuffer.Length < origSize)
                CompressionBuffer = new byte[origSize];
            LZ4Codec.Decode(data[2..], new Span<byte>(CompressionBuffer));
            _value ??= _constructor();
            _value.Deserialize(new SpanReader(new ReadOnlySpan<byte>(CompressionBuffer, 0, origSize)));
        }

        public static implicit operator T(SyncSpanSerializable<T> field)
        {
            return field._value;
        }
    }
}