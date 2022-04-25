using System;
using System.Collections;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public class LiteRingBuffer<T> : IEnumerable<T>
    {
        private readonly T[] _elements;
        private int _start;
        private int _end;
        private int _count;
        
        public readonly int Capacity;

        public ref T this[int i] => ref _elements[(_start + i) % Capacity];

        public void Fill(Func<T> construct)
        {
            for (int i = 0; i < Capacity; i++)
            {
                _elements[i] = construct();
            }
        }

        public LiteRingBuffer(int capacity)
        {
            _elements = new T[capacity];
            Capacity = capacity;
        }

        public ref T Add()
        {
            if(_count == Capacity)
                throw new ArgumentException();
            ref var res = ref _elements[_end];
            _end = (_end + 1) % Capacity;
            _count++;
            return ref res;
        }

        public void Add(T element)
        {
            if(_count == Capacity)
                throw new ArgumentException();
            _elements[_end] = element;
            _end = (_end + 1) % Capacity;
            _count++;
        }

        public void FastClear()
        {
            _start = 0;
            _end = 0;
            _count = 0;
        }

        public int Count => _count;
        public T First => _elements[_start];
        public T Last => _elements[(_start+_count-1)%Capacity];
        public bool IsFull => _count == Capacity;

        public void RemoveFromStart(int count)
        {
            if(count > Capacity || count > _count)
                throw new ArgumentException();
            _start = (_start + count) % Capacity;
            _count -= count;
        }

        public IEnumerator<T> GetEnumerator()
        {
            int counter = _start;
            while (counter != _end)
            {
                yield return _elements[counter];
                counter = (counter + 1) % Capacity;
            }           
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}