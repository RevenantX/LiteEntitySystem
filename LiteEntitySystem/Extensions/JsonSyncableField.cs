#if UNITY_2021_2_OR_NEWER
using System;
using System.Text;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    public class JsonSyncableField<T> : SyncableField where T : ScriptableObject
    {
        private static readonly UTF8Encoding Encoding = new(false, true);
        
        public T Value;

        private RemoteCallSpan<byte> _initAction;

        protected override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected override void OnSyncRequested()
        {
            if (Value == null)
                Value = ScriptableObject.CreateInstance<T>();
            
            string str = JsonUtility.ToJson(Value, false);
            byte[] stringData = new byte[Encoding.GetMaxByteCount(str.Length)];
            int size = Encoding.GetBytes(str, 0, str.Length, stringData, 0);
            ExecuteRPC(_initAction, new ReadOnlySpan<byte>(stringData, 0, size));
        }

        private void Init(ReadOnlySpan<byte> data)
        {
            LoadFromJson(Encoding.GetString(data));
        }

        private void LoadFromJson(string jsonString)
        {
            if (Value == null)
                Value = ScriptableObject.CreateInstance<T>();
            JsonUtility.FromJsonOverwrite(jsonString, Value);
        }

        public static implicit operator T(JsonSyncableField<T> field)
        {
            return field.Value;
        }
    }
}
#endif