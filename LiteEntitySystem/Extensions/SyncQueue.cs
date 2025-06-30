using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem.Extensions
{
    public class SyncQueue<T> : SyncableFieldCustomRollback, IReadOnlyCollection<T>, ICollection where T : unmanaged
    {
        // TODO: implement ring buffer instead of using .net's Queue.
        
        private Queue<T> _serverData;
        private Queue<T> _tempData;
        private Queue<T> _data = new();
        
        private static RemoteCall<T> _enqueueAction;
        private static RemoteCall _dequeueAction;
        private static RemoteCall _clearAction;
        private static RemoteCallSpan<T> _syncAction;
        
        public int Count => _data.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => throw new NotImplementedException("The SyncQueue Collection isn't thread-safe.");
        
        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.CreateClientAction(this, EnqueueClientAction, ref _enqueueAction);
            r.CreateClientAction(this, DequeueClientAction, ref _dequeueAction);
            r.CreateClientAction(this, ClearClientAction, ref _clearAction);
            r.CreateClientAction(this, SyncClientAction, ref _syncAction);
        }
        
        protected internal override void OnRollback()
        {
            _data.Clear();
            foreach (var item in _serverData)
                _data.Enqueue(item);
        }

        protected internal override void BeforeReadRPC()
        {
            _serverData ??= new Queue<T>();
            _tempData = _data;
            _data = _serverData;
        }

        protected internal override void AfterReadRPC()
        {
            _data = _tempData;
            _data.Clear();
            foreach (var item in _serverData)
                _data.Enqueue(item);
        }

        protected internal override unsafe void OnSyncRequested()
        {
            int count = 0;
            Span<T> temp = stackalloc T[_data.Count];
            foreach (var item in _data)
                temp[count++] = item;
            ExecuteRPC(_syncAction, temp);
        }

        private void SyncClientAction(ReadOnlySpan<T> data)
        {
            _data.Clear();
            foreach (var item in data)
                _data.Enqueue(item);
        }

        public void Enqueue(T item)
        {
            _data.Enqueue(item);
            ExecuteRPC(_enqueueAction, item);
            MarkAsChanged();
        }

        private void EnqueueClientAction(T item) => _data.Enqueue(item);

        public T Dequeue()
        {
            var value = _data.Dequeue();
            ExecuteRPC(_dequeueAction);
            MarkAsChanged();
            return value;
        }
        
        public bool TryDequeue(out T item)
        {
            bool hasValue = _data.TryDequeue(out item);
            if (hasValue)
            {
                ExecuteRPC(_dequeueAction);
                MarkAsChanged();
            }
            return hasValue;
        }

        private void DequeueClientAction() => _data.TryDequeue(out _);

        public T Peek() => _data.Peek();

        public bool TryPeek(out T item) => _data.TryPeek(out item);

        public bool Contains(T item) => _data.Contains(item);

        public void Clear()
        {
            _data.Clear();
            ExecuteRPC(_clearAction);
            MarkAsChanged();
        }

        private void ClearClientAction() => _data.Clear();
        
        public void CopyTo(Array array, int index) => ((ICollection)_data).CopyTo(array, index);

        public Queue<T>.Enumerator GetEnumerator() => _data.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _data.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _data.GetEnumerator();
    }
}