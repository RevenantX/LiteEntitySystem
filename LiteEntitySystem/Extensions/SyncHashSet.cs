using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem.Extensions
{
    public class SyncHashSet<T> : SyncableField, IEnumerable<T> where T : unmanaged
    {
        public int Count => _data.Count;

        private HashSet<T> _serverData;
        private HashSet<T> _tempData;
        private HashSet<T> _data = new ();

        private static RemoteCall<T> _addAction;
        private static RemoteCall _clearAction;
        private static RemoteCall<T> _removeAction;
        private static RemoteCallSpan<T> _initAction;

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, AddAction, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, RemoveAction, ref _removeAction);
            r.CreateClientAction(this, InitAction, ref _initAction);
        }

        protected internal override void OnRollback()
        {
            _data.Clear();
            foreach (var x in _serverData)
                _data.Add(x);
        }

        protected internal override void BeforeReadRPC()
        {
            _serverData ??= new HashSet<T>();
            _tempData = _data;
            _data = _serverData;
        }

        protected internal override void AfterReadRPC()
        {
            _data = _tempData;
            _data.Clear();
            foreach (var kv in _serverData)
                _data.Add(kv);
        }

        protected internal override unsafe void OnSyncRequested()
        {
            int cacheCount = 0;
            Span<T> kvCache = stackalloc T[_data.Count];
            foreach (var kv in _data)
                kvCache[cacheCount++] = kv;
            ExecuteRPC(_initAction, kvCache);
        }

        private void InitAction(ReadOnlySpan<T> data)
        {
            _data.Clear();
            foreach (var x in data)
                _data.Add(x);
        }

        private void AddAction(T x) =>  _data.Add(x);

        public void Add(T x)
        {
            _data.Add(x);
            ExecuteRPC(_addAction, x);
        }
        
        public void Clear()
        {
            _data.Clear();
            ExecuteRPC(_clearAction);
        }

        public bool Contains(T x) => _data.Contains(x);

        private void RemoveAction(T key) => _data.Remove(key);

        public bool Remove(T key)
        {
            if (!_data.Remove(key)) 
                return false;
            ExecuteRPC(_removeAction, key);
            return true;
        }

        public HashSet<T>.Enumerator GetEnumerator() => _data.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    }
}