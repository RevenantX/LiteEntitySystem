using System;
using System.Collections.Generic; // For EqualityComparer<T>
using K4os.Compression.LZ4;
using LiteNetLib.Utils;

namespace LiteEntitySystem.Extensions
{
    public class SyncNetSerializable<T> : SyncableField<T> where T : INetSerializable
    {
        private static readonly NetDataWriter WriterCache = new();
        private static readonly NetDataReader ReaderCache = new();
        private static byte[] CompressionBuffer;
        
        private T _value;

        private static RemoteCallSpan<byte> _initAction;

        private readonly Func<T> _constructor;

        public SyncNetSerializable(Func<T> constructor)
        {
            _constructor = constructor;
        }

        public override event EventHandler<SyncVarChangedEventArgs<T>> ValueChanged;

        /// <summary>
        /// The user-facing property; setting on the server will replicate out.
        /// </summary>
        public override T Value
        {
            get => _value;
            set
            {
                _value = value;
                OnSyncRequested();
            }
        }
        
        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected internal override void OnSyncRequested()
        {
            // Ensure we have an instance (for the first time)
            _value ??= _constructor();

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
            // Read uncompressed size
            ushort origSize = BitConverter.ToUInt16(data);

            if (CompressionBuffer == null || CompressionBuffer.Length < origSize)
                CompressionBuffer = new byte[origSize];
            LZ4Codec.Decode(data[2..], new Span<byte>(CompressionBuffer));
            ReaderCache.SetSource(CompressionBuffer, 0, origSize);

            // Capture the old reference
            T oldValue = _value;

            // Always create a fresh instance for deserialization
            T newValue = _constructor();
            newValue.Deserialize(ReaderCache);

            // Update _value
            _value = newValue;

            // Compare old and new. If changed, raise event.
            if (oldValue == null || !EqualityComparer<T>.Default.Equals(oldValue, _value))
            {
                ValueChanged?.Invoke(this, new SyncVarChangedEventArgs<T>(oldValue, _value));
            }
        }


        /// <summary>
        /// Allows implicit usage like "T t = mySyncNetSerializable;".
        /// </summary>
        public static implicit operator T(SyncNetSerializable<T> field)
        {
            return field._value;
        }
    }
}