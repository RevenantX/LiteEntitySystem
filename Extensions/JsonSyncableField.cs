#if UNITY_2021_3_OR_NEWER
using System;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    public class JsonSyncableField<T> : SyncableField where T : ScriptableObject
    {
        private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);
        
        public T Value;

        public void LoadFromJson(string jsonString)
        {
            if (Value == null)
                Value = ScriptableObject.CreateInstance<T>();
            JsonUtility.FromJsonOverwrite(jsonString, Value);
        }

        public override unsafe void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            if (Value == null)
            {
                Value = ScriptableObject.CreateInstance<T>();
            }
            var str = JsonUtility.ToJson(Value, false);

            byte[] stringData = new byte[Encoding.GetMaxByteCount(str.Length)];
            int size = Encoding.GetBytes(str, 0, str.Length, stringData, 0);

            fixed (byte* data = dataSpan, rawData = stringData)
            {
                *(ushort*)(data + position) = (ushort)size;
                Unsafe.CopyBlock(data + position + sizeof(ushort), rawData, (uint)size);
            }
            position += sizeof(ushort) + size;
        }

        public override unsafe void FullSyncRead(ReadOnlySpan<byte> dataSpan, ref int position)
        {
            fixed (byte* data = dataSpan)
            {
                int length = *(ushort*)(data + position);
                var str = Encoding.GetString(data + position + sizeof(ushort), length);
                position += sizeof(ushort) + length;
                LoadFromJson(str);
            }
        }

        public static implicit operator T(JsonSyncableField<T> field)
        {
            return field.Value;
        }
    }
}
#endif