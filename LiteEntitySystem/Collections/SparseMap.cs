using System;

namespace LiteEntitySystem.Collections
{
    public class SparseMap<T>
    {
        public struct SparseEntry
        {
            public int Id;
            public T Value;
        }
        
        private int[] _sparse;
        private SparseEntry[] _dense;
        private int _count;

        public int Count => _count;

        public SparseMap() : this(8)
        {
            
        }

        public SparseMap(int capacity)
        {
            _sparse = new int[capacity];
            _dense = new SparseEntry[capacity];
        }

        public void CopyTo(SparseMap<T> target)
        {
            target.Clear();
            for (int i = 0; i < _count; i++)
                target.Set(_dense[i].Id, _dense[i].Value);
        }

        public ref T GetByIndex(int index)
        {
            if (index >= _count)
                throw new IndexOutOfRangeException();
            return ref _dense[index].Value;
        }

        public ref T GetById(int id)
        {
            int index = _sparse[id];
            if (index < _count && _dense[index].Id == id)
                return ref _dense[index].Value;
            throw new ArgumentOutOfRangeException($"Id: {id} not found in map");
        }
        
        public ref readonly SparseEntry GetSparseEntryByIndex(int index)
        {
            if (index >= _count)
                throw new IndexOutOfRangeException();
            return ref _dense[index];
        }
        
        public bool TryGetSparseEntry(int id, out SparseEntry result)
        {
            result = default;
            if (id >= _sparse.Length)
                return false;
            int index = _sparse[id];
            if (index < _count && _dense[index].Id == id)
            {
                result = _dense[index];
                return true;
            }
            return false;
        }

        public int FindIndex(int id)
        {
            if (id >= _sparse.Length)
                return -1;
            int index = _sparse[id];
            return index < _count && _dense[index].Id == id ? index : -1;
        }

        public bool Contains(int id)
        {
            if (id >= _sparse.Length)
                return false;
            int index = _sparse[id];
            return index < _count && _dense[index].Id == id;
        }

        public bool TryGetValue(int id, out T result)
        {
            result = default;
            if (id >= _sparse.Length)
                return false;
            int index = _sparse[id];
            if (index < _count && _dense[index].Id == id)
            {
                result = _dense[index].Value;
                return true;
            }
            return false;
        }

        public void Set(int id, T value)
        {
            if (_count == _dense.Length)
                Array.Resize(ref _dense, _count * 2);
            if (id >= _sparse.Length)
                Array.Resize(ref _sparse, Math.Max(_sparse.Length*2, id+1));   
            
            int i = _sparse[id];
            if (i < _count && _dense[i].Id == id)
            {
                _dense[i].Value = value;
                return;
            }
            _dense[_count] = new SparseEntry { Id = id, Value = value };
            _sparse[id] = _count;
            _count++;
        }
 
        public bool Remove(int id)
        {
            if (id >= _sparse.Length)
                return false;
            int i = _sparse[id];
            if (i < _count && _dense[i].Id == id)
            {
                _count--;
                _dense[i] = _dense[_count];
                _sparse[_dense[_count].Id] = i;
                return true;
            }
            return false;
        }

        public bool Remove(int id, out T removedElement)
        {
            removedElement = default;
            if (id >= _sparse.Length)
                return false;
            int i = _sparse[id];
            if (i < _count && _dense[i].Id == id)
            {
                removedElement = _dense[i].Value;
                _count--;
                _dense[i] = _dense[_count];
                _sparse[_dense[_count].Id] = i;
                return true;
            }
            return false;
        }

        public void Clear()
        {
            _count = 0;
        }
    }
}