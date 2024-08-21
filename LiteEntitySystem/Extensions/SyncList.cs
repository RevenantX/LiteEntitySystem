using System;
using System.Collections;
using System.Collections.Generic;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Extensions
{
    public class SyncList<T> : SyncableField, ICollection<T>, IReadOnlyList<T> where T : unmanaged
    {
        struct SetValueData
        {
            public int Index;
            public T Data;
        }
        
        public int Count => _count;
        public bool IsReadOnly => false;

        private T[] _serverData;
        private T[] _data;
        private T[] _temp;
        private int _serverCount;
        private int _count;

        private static RemoteCall<T> _addAction;
        private static RemoteCall _clearAction;
        private static RemoteCall<int> _removeAtAction;
        private static RemoteCallSpan<T> _initAction;
        private static RemoteCall<SetValueData> _setAction;

        protected internal override void BeforeReadRPC()
        {
            _serverData ??= new T[_data.Length];
            _temp = _data;
            _data = _serverData;
            _count = _serverCount;
        }

        protected internal override unsafe void AfterReadRPC()
        {
            _serverCount = _count;
            _serverData = _data;
            _data = _temp;
            if (_data.Length < _serverData.Length)
                Array.Resize(ref _data, _serverData.Length);
            fixed (void* serverData = _serverData, data = _data)
                RefMagic.CopyBlock(data, serverData, (uint)(_count * sizeof(T)));
        }

        protected internal override unsafe void OnRollback()
        {
            _count = _serverCount;
            fixed (void* serverData = _serverData, data = _data)
                RefMagic.CopyBlock(data, serverData, (uint)(_count * sizeof(T)));
        }

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Add, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, RemoveAt, ref _removeAtAction);
            r.CreateClientAction(this, Init, ref _initAction);
            r.CreateClientAction(this, Set, ref _setAction);
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

        private void Set(SetValueData svd)
        {
            _data[svd.Index] = svd.Data;
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
        
        public T this[int index]
        {
            get => _data[index];
            set
            {
                _data[index] = value;
                ExecuteRPC(_setAction, new SetValueData { Index = index, Data = value });
            }
        }

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