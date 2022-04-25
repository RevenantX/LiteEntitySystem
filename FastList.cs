using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public class FastList<T> : IList<T>
    {
        public T[] Data;
        private int _count;
        public int Count => _count;
        public bool IsReadOnly => false;

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }

        public FastList()
        {
            Data = new T[8];
        }

        public FastList(int capacity)
        {
            Data = new T[capacity];
        }

        public int AddCount(int count)
        {
            int prevCount = _count;
            _count += count;
            int len = Data.Length;
            if (len < _count)
            {
                while (len < _count)
                    len *= 2;
                System.Array.Resize(ref Data, len);
            }
            return prevCount;
        }

        public void CloneFrom(T[] array)
        {
            _count = array.Length;
            if (_count > Data.Length)
                System.Array.Resize(ref Data, _count);
            for (int i = 0; i < _count; i++)
                Data[i] = array[i];
        }

        public void CloneFrom(T[] array, int count)
        {
            _count = count;
            if (_count > Data.Length)
                System.Array.Resize(ref Data, _count);
            for (int i = 0; i < _count; i++)
                Data[i] = array[i];
        }

        public void CloneFrom(List<T> list)
        {
            _count = list.Count;
            if (_count > Data.Length)
                System.Array.Resize(ref Data, _count);
            for (int i = 0; i < _count; i++)
                Data[i] = list[i];
        }

        public void CloneFrom(FastList<T> list)
        {
            _count = list._count;
            if (_count > Data.Length)
                System.Array.Resize(ref Data, _count);
            for (int i = 0; i < _count; i++)
                Data[i] = list.Data[i];
        }

        public T[] ToArray()
        {
            var arr = new T[_count];
            for (int i = 0; i < _count; i++)
                arr[i] = Data[i];
            return arr;
        }

        public void Add(T item)
        {
            if (Data.Length == _count)
                Array.Resize(ref Data, Data.Length * 2);
            Data[_count] = item;
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
            Array.Clear(Data, 0, _count);
            _count = 0;
        }

        public bool Contains(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (Data[i].Equals(item))
                    return true;
            }
            return false;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(Data, 0, array, arrayIndex, _count);
        }

        public bool Remove(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (Data[i].Equals(item))
                {
                    Data[i] = Data[_count - 1];
                    Data[_count - 1] = default;
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
                if (Data[i].Equals(item))
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
            Data[index] = Data[_count - 1];
            Data[_count - 1] = default;
            _count--;
        }

        public T this[int index]
        {
            get => Data[index];
            set => Data[index] = value;
        }
    }
}