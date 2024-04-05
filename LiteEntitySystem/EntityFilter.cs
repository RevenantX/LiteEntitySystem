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
        private enum EntityFilterOp
        {
            Add,
            Remove
        }

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
        /// <param name="callOnExisting">call that callback on existing entities in this filter/list</param>
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
        /// Called when entity created and synced
        /// <param name="onConstructed">callback</param>
        /// </summary>
        public void UnsubscribeToConstructed(Action<T> onConstructed) =>
            OnConstructed -= onConstructed;
        
        /// <summary>
        /// Entities count of type <typeparamref name="T"/>
        /// </summary>
        public int Count => _entities.Count;

        private (T entity, EntityFilterOp operation)[] _entityOperations;
        private int _entityOperationsCount;

        internal override void Add(InternalEntity entity)
        {
            Utils.ResizeOrCreate(ref _entityOperations, _entityOperationsCount + 1);
            _entityOperations[_entityOperationsCount++] = ((T)entity, EntityFilterOp.Add);
            OnConstructed?.Invoke((T)entity);
        }

        internal override void Remove(InternalEntity entity)
        {
            Utils.ResizeOrCreate(ref _entityOperations, _entityOperationsCount + 1);
            _entityOperations[_entityOperationsCount++] = ((T)entity, EntityFilterOp.Remove);
            OnDestroyed?.Invoke((T)entity);
        }

        public T[] ToArray()
        {
            Refresh();
            var resultArr = new T[_entities.Count];
            int idx = 0;
            foreach (var entity in this)
                resultArr[idx++] = entity;
            return resultArr;
        }

        public bool Contains(T entity) =>
            _entities.Contains(entity);

        internal override void Clear()
        {
            _entityOperationsCount = 0;
            _entities.Clear();
            _enumerator = _entities.GetEnumerator();
        }

        internal bool Refresh()
        {
            if (_entityOperationsCount > 0)
            {
                for (int i = 0; i < _entityOperationsCount; i++)
                    if (_entityOperations[i].operation == EntityFilterOp.Add)
                        _entities.Add(_entityOperations[i].entity);
                    else
                        _entities.Remove(_entityOperations[i].entity);

                _entityOperationsCount = 0;
                _enumerator = _entities.GetEnumerator();
                return true;
            }

            return false;
        }

        private static void ResetEnumerator<TEnumerator>(ref TEnumerator enumerator) where TEnumerator : struct, IEnumerator<T> =>
            enumerator.Reset();

        public SortedSet<T>.Enumerator GetEnumerator()
        {
            if (Refresh() == false)
                ResetEnumerator(ref _enumerator);
            return _enumerator;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() =>
            GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();
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

        public new void Clear() =>
            base.Clear();
    }
}