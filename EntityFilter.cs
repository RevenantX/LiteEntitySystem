using System;
using System.Collections;
using System.Collections.Generic;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public abstract class EntityFilter
    {
        internal abstract void Add(InternalEntity entity);
        internal abstract void Remove(InternalEntity entity);
    }

    //For usability
    public sealed class EntityFilter<T> : EntityFilter, IEnumerable<T> where T : InternalEntity
    {
        public struct EntityFilterEnumerator : IEnumerator<T>
        {
            private int _idx;
            private readonly EntityFilter<T> _filter;

            public EntityFilterEnumerator(EntityFilter<T> filter)
            {
                _filter = filter;
                _idx = -1;
            }

            public bool MoveNext()
            {
                _idx++;
                return _idx < _filter._count;
            }

            public void Reset()
            {
                _idx = -1;
            }

            public T Current => _filter._array[_idx];

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                
            }
        }

        public event Action<T> OnAdded;
        public event Action<T> OnRemoved;
        
        private ushort[] _dict;
        private T[] _array = new T[8];
        private ushort _count;
        
        internal override void Add(InternalEntity entity)
        {
            if (_dict == null)
            {
                _dict = new ushort[Math.Max(entity.Id * 2, 8)];
            }
            else if (entity.Id >= _dict.Length)
            {
                Array.Resize(ref _dict, Math.Min(entity.Id * 2, EntityManager.MaxEntityCount));
            }

            if (_count == _array.Length)
            {
                Array.Resize(ref _array, Math.Min(_count * 2, EntityManager.MaxEntityCount));
            }

            _array[_count] = (T)entity;
            _dict[entity.Id] = _count;
            _count++;
            
            OnAdded?.Invoke((T)entity);
        }

        internal override void Remove(InternalEntity entity)
        {
            ushort idx = _dict[entity.Id];
            _count--;
            if(idx != _count)
            {
                _array[idx] = _array[_count];
                _dict[_array[idx].Id] = idx;
            }
            _array[_count] = null;
            OnRemoved?.Invoke((T)entity);
        }

        public EntityFilterEnumerator GetEnumerator()
        {
            return new EntityFilterEnumerator(this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new EntityFilterEnumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new EntityFilterEnumerator(this);
        }
    }
}