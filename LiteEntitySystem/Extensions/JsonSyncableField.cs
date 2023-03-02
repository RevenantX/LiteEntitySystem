#if UNITY_2021_2_OR_NEWER
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

        public override int GetFullSyncSize()
        {
            if (Value == null)
                Value = ScriptableObject.CreateInstance<T>();
            return Encoding.GetByteCount(JsonUtility.ToJson(Value, false));
        }

        public override unsafe void FullSyncWrite(ServerEntityManager server, Span<byte> dataSpan)
        {
            if (Value == null)
                Value = ScriptableObject.CreateInstance<T>();
            
            string str = JsonUtility.ToJson(Value, false);
            byte[] stringData = new byte[Encoding.GetMaxByteCount(str.Length)];
            int size = Encoding.GetBytes(str, 0, str.Length, stringData, 0);

            fixed (byte* data = dataSpan, rawData = stringData)
                Unsafe.CopyBlock(data, rawData, (uint)size);
        }

        public override unsafe void FullSyncRead(ClientEntityManager client, ReadOnlySpan<byte> dataSpan)
        {
            fixed (byte* data = dataSpan)
                LoadFromJson(Encoding.GetString(data, dataSpan.Length));
        }

        public static implicit operator T(JsonSyncableField<T> field)
        {
            return field.Value;
        }
    }
}
#endif