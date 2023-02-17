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
        internal abstract void Clear();
    }

    public class EntityFilter<T> : EntityFilter, IEnumerable<T> where T : InternalEntity
    {
        private readonly SortedSet<T> _entities = new();
        private SortedSet<T>.Enumerator _enumerator;

        public EntityFilter()
        {
            _enumerator = _entities.GetEnumerator();
        }
        
        private event Action<T> OnConstructed;
        
        /// <summary>
        /// Called when entity is removed/destroyed
        /// </summary>
        public event Action<T> OnDestroyed;

        /// <summary>
        /// Called when entity created and synced
        /// <param name="onConstructed">callback</param>
        /// <param name="callOnExisting">call that callback on existing entities in this fitler/list</param>
        /// </summary>
        public void SubscribeToConstructed(Action<T> onConstructed, bool callOnExisting)
        {
            if (callOnExisting)
            {
                foreach (T entity in this)
                {
                    onConstructed(entity);
                }
            }
            OnConstructed += onConstructed;
        }
        
        /// <summary>
        /// Entities count of type <typeparamref name="T"/>
        /// </summary>
        public int Count => _entities.Count;

        private T[] _entitiesToAdd;
        private T[] _entitiesToRemove;
        private int _entitiesToAddCount;
        private int _entitiesToRemoveCount;

        internal override void Add(InternalEntity entity)
        {
            Utils.ResizeOrCreate(ref _entitiesToAdd, _entitiesToAddCount+1);
            _entitiesToAdd[_entitiesToAddCount++] = (T)entity;
            OnConstructed?.Invoke((T)entity);
        }

        internal override void Remove(InternalEntity entity)
        {
            Utils.ResizeOrCreate(ref _entitiesToRemove, _entitiesToRemoveCount + 1);
            _entitiesToRemove[_entitiesToRemoveCount++] = (T)entity;
            OnDestroyed?.Invoke((T)entity);
        }
        
        public T[] ToArray()
        {
            var resultArr = new T[_entities.Count + _entitiesToAddCount - _entitiesToRemoveCount];
            int idx = 0;
            foreach (var entity in this)
            {
                resultArr[idx++] = entity;
            }
            return resultArr;
        }

        public bool Contains(T entity)
        {
            return _entities.Contains(entity);
        }

        internal override void Clear()
        {
            _entitiesToAddCount = 0;
            _entitiesToRemoveCount = 0;
            _entities.Clear();
            _enumerator = _entities.GetEnumerator();
        }

        internal void Refresh()
        {
            if (_entitiesToAddCount > 0 || _entitiesToRemoveCount > 0)
            {
                for (int i = 0; i < _entitiesToAddCount; i++)
                {
                    _entities.Add(_entitiesToAdd[i]);
                }
                for (int i = 0; i < _entitiesToRemoveCount; i++)
                {
                    _entities.Remove(_entitiesToRemove[i]);
                }
                _entitiesToAddCount = 0;
                _entitiesToRemoveCount = 0;
                _enumerator = _entities.GetEnumerator();
            }
        }
        
        private static void ResetEnumerator<TEnumerator>(ref TEnumerator enumerator) where TEnumerator : struct, IEnumerator<T>
        {
            enumerator.Reset();
        }

        public SortedSet<T>.Enumerator GetEnumerator()
        {
            if (_entitiesToAddCount > 0 || _entitiesToRemoveCount > 0)
            {
                Refresh();
            }
            else
            {
                ResetEnumerator(ref _enumerator);
            }
            return _enumerator;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public class EntityList<T> : EntityFilter<T> where T : InternalEntity
    {
        public new void Add(InternalEntity entity)
        {
            base.Add(entity);
            Refresh();
        }
        
        public new void Remove(InternalEntity entity)
        {
            base.Remove(entity);
            Refresh();
        }

        public new void Clear()
        {
            base.Clear();
        }
    }
}