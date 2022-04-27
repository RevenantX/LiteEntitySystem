using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public class SyncList<T> where T : struct
    {
        private T[] _data;
        private int _count;
        public int Count => _count;
    
        /*
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
        */

        public SyncList()
        {
            _data = new T[8];
        }

        public SyncList(int capacity)
        {
            _data = new T[capacity];
        }

        public void CloneFrom(T[] array)
        {
            _count = array.Length;
            if (_count > _data.Length)
                Array.Resize(ref _data, _count);
            for (int i = 0; i < _count; i++)
                _data[i] = array[i];
        }

        public void CloneFrom(T[] array, int count)
        {
            _count = count;
            if (_count > _data.Length)
                Array.Resize(ref _data, _count);
            for (int i = 0; i < _count; i++)
                _data[i] = array[i];
        }

        public void CloneFrom(List<T> list)
        {
            _count = list.Count;
            if (_count > _data.Length)
                Array.Resize(ref _data, _count);
            for (int i = 0; i < _count; i++)
                _data[i] = list[i];
        }

        public void CloneFrom(SyncList<T> list)
        {
            _count = list._count;
            if (_count > _data.Length)
                Array.Resize(ref _data, _count);
            for (int i = 0; i < _count; i++)
                _data[i] = list._data[i];
        }

        public T[] ToArray()
        {
            var arr = new T[_count];
            for (int i = 0; i < _count; i++)
                arr[i] = _data[i];
            return arr;
        }

        public void Add(T item)
        {
            if (_data.Length == _count)
                Array.Resize(ref _data, _data.Length * 2);
            _data[_count] = item;
            _count++;
        }

        public void Clear()
        {
            _count = 0;
        }

        public void FullClear()
        {
            if (_count == 0)
                return;
            Array.Clear(_data, 0, _count);
            _count = 0;
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
                    _data[i] = _data[_count - 1];
                    _data[_count - 1] = default;
                    _count--;
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
        }

        public ref T this[int index] => ref _data[index];
    }
}