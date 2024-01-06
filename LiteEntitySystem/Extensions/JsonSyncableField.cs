#if UNITY_2021_2_OR_NEWER
using System;
using System.Text;
using K4os.Compression.LZ4;
using LiteNetLib.Utils;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    public class JsonSyncableField<T> : SyncableField where T : ScriptableObject
    {
        private static readonly UTF8Encoding Encoding = new(false, true);
        private static byte[] StringBuffer;
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

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected internal override void OnSyncRequested()
        {
            if (_value == null)
                _value = ScriptableObject.CreateInstance<T>();
            
            string str = JsonUtility.ToJson(_value, false);
            int maxBytes = Encoding.GetMaxByteCount(str.Length);
            if (StringBuffer == null || StringBuffer.Length < maxBytes)
            {
                StringBuffer = new byte[maxBytes];
                CompressionBuffer = new byte[LZ4Codec.MaximumOutputSize(maxBytes) + 4];
            }
            int size = Encoding.GetBytes(str, 0, str.Length, StringBuffer, 0);
            FastBitConverter.GetBytes(CompressionBuffer, 0, size);
            int encodedLength = LZ4Codec.Encode(
                StringBuffer,
                0,
                size,
                CompressionBuffer,
                4,
                CompressionBuffer.Length-4,
                LZ4Level.L00_FAST);
            ExecuteRPC(_initAction, new ReadOnlySpan<byte>(CompressionBuffer, 0, encodedLength+4));
        }

        private void Init(ReadOnlySpan<byte> data)
        {
            int origSize = BitConverter.ToInt32(data);
            if (CompressionBuffer == null || CompressionBuffer.Length < origSize)
                CompressionBuffer = new byte[origSize];
            LZ4Codec.Decode(data[4..], new Span<byte>(CompressionBuffer));
            
            if (_value == null)
                _value = ScriptableObject.CreateInstance<T>();
            JsonUtility.FromJsonOverwrite(Encoding.GetString(CompressionBuffer, 0, origSize), _value);
        }

        public static implicit operator T(JsonSyncableField<T> field)
        {
            return field._value;
        }
    }
}
#endif