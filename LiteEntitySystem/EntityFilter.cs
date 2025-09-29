using System;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public interface IEntityFilter
    { 
        internal void Add(InternalEntity entity);
        internal void Remove(InternalEntity entity);
        internal void TriggerConstructedEvent(InternalEntity entity);
    }

    //SortedSet like collection based on AVLTree
    public class EntityFilter<T> : AVLTree<T>, IEntityFilter where T : InternalEntity
    {
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
                foreach (var entity in this)
                    if(entity.CreationState == EntityState.LateConstructed)
                        onConstructed(entity);
            OnConstructed += onConstructed;
        }
        
        /// <summary>
        /// Called when entity created and synced
        /// <param name="onConstructed">callback</param>
        /// </summary>
        public void UnsubscribeToConstructed(Action<T> onConstructed) =>
            OnConstructed -= onConstructed;
        
        internal override bool Remove(T entity)
        {
            OnDestroyed?.Invoke(entity);
            return base.Remove(entity);
        }

        void IEntityFilter.Add(InternalEntity entity) => Add((T) entity);
        void IEntityFilter.Remove(InternalEntity entity) => Remove((T) entity);
        void IEntityFilter.TriggerConstructedEvent(InternalEntity entity) => OnConstructed?.Invoke((T)entity);

        internal override void Clear()
        {
            OnConstructed = null;
            OnDestroyed = null;
            base.Clear();
        }
    }
}