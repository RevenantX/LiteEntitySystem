using System;
using System.Collections;
using System.Collections.Generic;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Extensions
{
    public class SyncList<T> : SyncableField, ICollection<T>, IReadOnlyList<T> where T : unmanaged
    {
        public int Count => _count;
        public bool IsReadOnly => false;

        private T[] _data;
        private int _count;

        private RemoteCall<T> _addAction;
        private RemoteCall _clearAction;
        private RemoteCall<int> _removeAtAction;
        private RemoteCallSpan<T> _initAction;

        protected override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Add, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, RemoveAt, ref _removeAtAction);
            r.CreateClientAction(this, Init, ref _initAction);
        }

        protected internal override void OnSyncRequested()
        {
            ExecuteRPC(_initAction, new ReadOnlySpan<T>(_data, 0, _count));
        }

        private void Init(ReadOnlySpan<T> data)
        {
            Utils.ResizeIfFull(ref _data, data.Length);
            data.CopyTo(_data);
            _count = data.Length;
        }

        public SyncList()
        {
            _data = new T[8];
        }

        public SyncList(int capacity)
        {
            _data = new T[capacity];
        }

        public T[] ToArray()
        {
            var arr = new T[_count];
            Buffer.BlockCopy(_data, 0, arr, 0, _count);
            return arr;
        }
        
        public void Add(T item)
        {
            if (_data.Length == _count)
                Array.Resize(ref _data, Math.Min(_data.Length * 2, ushort.MaxValue));
            _data[_count] = item;
            _count++;
            ExecuteRPC(_addAction, item);
        }
        
        public void Clear()
        {
            _count = 0;
            ExecuteRPC(_clearAction);
        }

        public bool Contains(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                    return true;
            }
            return false;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(_data, 0, array, arrayIndex, _count);
        }
        
        public bool Remove(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                {
                    RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public int IndexOf(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                    return i;
            }
            return -1;
        }

        public void RemoveAt(int index)
        {
            _data[index] = _data[_count - 1];
            _data[_count - 1] = default;
            _count--;
            ExecuteRPC(_removeAtAction, index);
        }

        public ref T this[int index] => ref _data[index];
        T IReadOnlyList<T>.this[int index] => _data[index];

        public IEnumerator<T> GetEnumerator()
        {
            int index = 0;
            while (index < _count)
            {
                yield return _data[index];
                index++;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}