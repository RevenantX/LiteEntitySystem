using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public sealed class SyncChilds : SyncableField, IEnumerable<EntitySharedReference>
    {
        class EqualityComparer : IEqualityComparer<EntitySharedReference>
        {
            public bool Equals(EntitySharedReference x, EntitySharedReference y) => x.Id == y.Id && x.Version == y.Version;
            public int GetHashCode(EntitySharedReference obj) => obj.GetHashCode();
        }
        
        public int Count => _data?.Count ?? 0;

        // ReSharper disable once CollectionNeverUpdated.Local
        private static readonly HashSet<EntitySharedReference> TempSetForEnumerator = new(0);
        private static readonly EqualityComparer SharedReferenceComparer = new();

        private HashSet<EntitySharedReference> _serverData;
        private HashSet<EntitySharedReference> _tempData;
        private HashSet<EntitySharedReference> _data;

        private static RemoteCall<EntitySharedReference> _addAction;
        private static RemoteCall _clearAction;
        private static RemoteCall<EntitySharedReference> _removeAction;
        private static RemoteCallSpan<EntitySharedReference> _initAction;
        
        public override bool IsRollbackSupported => true;

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Add, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, RemoveAction, ref _removeAction);
            r.CreateClientAction(this, InitAction, ref _initAction);
        }

        protected internal override void OnRollback()
        {
            if (_data == null || _serverData == null)
                return;
            _data.Clear();
            foreach (var x in _serverData)
                _data.Add(x);
        }

        protected internal override void BeforeReadRPC()
        {
            if (_data == null)
                return;
            _serverData ??= new HashSet<EntitySharedReference>(SharedReferenceComparer);
            _tempData = _data;
            _data = _serverData;
        }

        protected internal override void AfterReadRPC()
        {
            if (_data == null || _tempData == null)
                return;
            _data = _tempData;
            _data.Clear();
            foreach (var kv in _serverData)
                _data.Add(kv);
        }

        protected internal override unsafe void OnSyncRequested()
        {
            if (_data == null || _data.Count == 0)
                return;
            int cacheCount = 0;
            Span<EntitySharedReference> kvCache = stackalloc EntitySharedReference[_data.Count];
            foreach (var kv in _data)
                kvCache[cacheCount++] = kv;
            ExecuteRPC(_initAction, kvCache);
        }

        private void InitAction(ReadOnlySpan<EntitySharedReference> data)
        {
            if (_data == null)
            {
                if (data.IsEmpty)
                    return;
                _data = new HashSet<EntitySharedReference>(SharedReferenceComparer);
            }
            else
            {
                _data.Clear();
            }
            foreach (var x in data)
                _data.Add(x);
        }

        /// <summary>
        /// To array
        /// </summary>
        /// <returns>hashset copied array. Returns null if HashSet is empty</returns>
        public EntitySharedReference[] ToArray()
        {
            if (_data == null || _data.Count == 0)
                return null;
            var arr = new EntitySharedReference[_data.Count];
            int idx = 0;
            foreach (var x in _data)
                arr[idx++] = x;
            return arr;
        }

        internal void Add(EntitySharedReference x)
        {
            _data ??= new HashSet<EntitySharedReference>(SharedReferenceComparer);
            _data.Add(x);
            ExecuteRPC(_addAction, x);
        }
        
        internal void Clear()
        {
            if (_data == null)
                return;
            _data.Clear();
            ExecuteRPC(_clearAction);
        }

        public bool Contains(EntitySharedReference x) => _data != null && _data.Contains(x);

        private void RemoveAction(EntitySharedReference key) => _data?.Remove(key);

        internal bool Remove(EntitySharedReference key)
        {
            if (_data == null || !_data.Remove(key)) 
                return false;
            ExecuteRPC(_removeAction, key);
            return true;
        }

        public HashSet<EntitySharedReference>.Enumerator GetEnumerator() => _data?.GetEnumerator() ?? TempSetForEnumerator.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        IEnumerator<EntitySharedReference> IEnumerable<EntitySharedReference>.GetEnumerator() => GetEnumerator();
    }
}