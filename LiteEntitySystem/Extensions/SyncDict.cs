using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem.Extensions
{
    public partial class SyncDict<TKey,TValue> : SyncableField, IEnumerable<KeyValuePair<TKey,TValue>> where TKey : unmanaged where TValue : unmanaged
    {
        private struct KeyValue
        {
            public TKey Key;
            public TValue Value;

            public KeyValue(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
        }
        
        public int Count => _data.Count;

        private readonly Dictionary<TKey, TValue> _data = new ();

        private RemoteCall<KeyValue> _addAction;
        private RemoteCall _clearAction;
        private RemoteCall<TKey> _removeAction;
        private RemoteCallSpan<KeyValue> _initAction;

        public Dictionary<TKey, TValue>.KeyCollection Keys => _data.Keys;
        public Dictionary<TKey, TValue>.ValueCollection Values => _data.Values;
        private static KeyValue[] KvCache = new KeyValue[8];

        protected internal override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, AddAction, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, RemoveAction, ref _removeAction);
            r.CreateClientAction(this, InitAction, ref _initAction);
        }

        protected internal override void OnSyncRequested()
        {
            int cacheCount = 0;
            if (_data.Count > KvCache.Length)
            {
                KvCache = new KeyValue[_data.Count];
            }
            foreach (var kv in _data)
            {
                KvCache[cacheCount++] = new KeyValue(kv.Key, kv.Value);
            }
            ExecuteRPC(_initAction, new ReadOnlySpan<KeyValue>(KvCache, 0, cacheCount));
        }

        private void InitAction(ReadOnlySpan<KeyValue> data)
        {
            _data.Clear();
            foreach (var kv in data)
            {
                _data.Add(kv.Key, kv.Value);
            }
        }

        private void AddAction(KeyValue kv)
        {
            _data[kv.Key] = kv.Value;
        }

        public void Add(TKey key, TValue value)
        {
            _data.Add(key, value);
            ExecuteRPC(_addAction, new KeyValue(key,value));
        }
        
        public void Clear()
        {
            _data.Clear();
            ExecuteRPC(_clearAction);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            return _data.TryGetValue(key, out value);
        }

        public bool ContainsKey(TKey key)
        {
            return _data.ContainsKey(key);
        }
        
        public bool ContainsValue(TValue value)
        {
            return _data.ContainsValue(value);
        }

        private void RemoveAction(TKey key)
        {
            _data.Remove(key);
        }

        public bool Remove(TKey key)
        {
            if (!_data.Remove(key)) 
                return false;
            ExecuteRPC(_removeAction, key);
            return true;
        }

        public TValue this[TKey index]
        {
            get => _data[index];
            set
            {
                _data[index] = value;
                ExecuteRPC(_addAction, new KeyValue(index, value));
            }
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return _data.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}