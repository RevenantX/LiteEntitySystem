using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Extensions
{
    public class SyncList<T> : SyncableField, IList<T>, IReadOnlyList<T> where T : unmanaged
    {
        public int Count => _count;
        public bool IsReadOnly => false;

        private T[] _data;
        private int _count;

        private RemoteCall<T> _addAction;
        private RemoteCall _clearAction;
        private RemoteCall _fullClearAction;
        private RemoteCall<int> _removeAtAction;

        public override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, Add, ref _addAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
            r.CreateClientAction(this, FullClear, ref _fullClearAction);
            r.CreateClientAction(this, RemoveAt, ref _removeAtAction);
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
        
        public void FullClear()
        {
            if (_count == 0)
                return;
            Array.Clear(_data, 0, _count);
            _count = 0;
            ExecuteRPC(_fullClearAction);
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

        public void Insert(int index, T item)
        {
            throw new NotImplementedException();
        }
        
        public void RemoveAt(int index)
        {
            _data[index] = _data[_count - 1];
            _data[_count - 1] = default;
            _count--;
            ExecuteRPC(_removeAtAction, index);
        }

        T IList<T>.this[int index]
        {
            get => _data[index];
            set => _data[index] = value;
        }

        public ref T this[int index] => ref _data[index];
        T IReadOnlyList<T>.this[int index] => _data[index];

        public override unsafe int GetFullSyncSize()
        {
            return sizeof(T) * _count;
        }

        public override unsafe void FullSyncWrite(ServerEntityManager server, Span<byte> dataSpan)
        {
            byte[] byteData = Unsafe.As<byte[]>(_data);
            fixed (byte* data = dataSpan)
            {
                fixed(void* rawData = byteData)
                    Unsafe.CopyBlock(data, rawData, (uint)dataSpan.Length);
            }
        }

        public override unsafe void FullSyncRead(ClientEntityManager client, ReadOnlySpan<byte> dataSpan)
        {
            _count = dataSpan.Length / sizeof(T);
            if (_data.Length < _count)
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _count));
            byte[] byteData = Unsafe.As<byte[]>(_data);
            fixed (byte* data = dataSpan)
            {
                fixed (void* rawData = byteData)
                    Unsafe.CopyBlock(rawData, data, (uint)dataSpan.Length);
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}