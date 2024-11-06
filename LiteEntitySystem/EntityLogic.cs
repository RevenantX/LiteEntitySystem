using System;
using System.Collections.Generic;
using System.Linq;
using LiteEntitySystem.Extensions;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [Flags]
    public enum EntityFlags
    {
        /// <summary>
        /// Update entity on client even when entity isn't owned
        /// </summary>
        UpdateOnClient = Updateable | (1 << 0), 
        
        /// <summary>
        /// Update entity on server and on client if entity is owned 
        /// </summary>
        Updateable = 1 << 1,                    
        
        /// <summary>
        /// Sync entity only for owner player
        /// </summary>
        OnlyForOwner = 1 << 2
    }
    
    [AttributeUsage(AttributeTargets.Class)]
    public class EntityFlagsAttribute : Attribute
    {
        public readonly EntityFlags Flags;
        
        public EntityFlagsAttribute(EntityFlags flags)
        {
            Flags = flags;
        }
    }
    
    /// <summary>
    /// Base class for simple (not controlled by controller) entity
    /// </summary>
    public abstract class EntityLogic : InternalEntity
    {
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        private SyncVar<EntitySharedReference> _parentId;

        /// <summary>
        /// Child entities (can be used for transforms or as components)
        /// </summary>
        public readonly SyncHashSet<EntitySharedReference> Childs = new();

        public EntitySharedReference ParentId => _parentId;
        
        private bool _lagCompensationEnabled;
        
        public EntitySharedReference SharedReference => new EntitySharedReference(this);

        [SyncVarFlags(SyncFlags.OnlyForOwner)]
        private SyncVar<ushort> _localPredictedIdCounter;
        [SyncVarFlags(SyncFlags.OnlyForOwner)]
        private SyncVar<ushort> _predictedId;
        
        internal ulong PredictedId => _predictedId.Value;
        
        //on client it works only in rollback
        internal void EnableLagCompensation(NetPlayer player)
        {
            if (_lagCompensationEnabled || InternalOwnerId.Value == player.Id)
                return;
            ushort tick = EntityManager.IsClient ? ClientManager.ServerTick : EntityManager.Tick;
            if (Utils.SequenceDiff(player.StateATick, tick) >= 0 || Utils.SequenceDiff(player.StateBTick, tick) > 0)
            {
                Logger.Log($"LagCompensationMiss. Tick: {tick}, StateA: {player.StateATick}, StateB: {player.StateBTick}");
                return;
            }
            ClassData.LoadHistroy(player, this);
            OnLagCompensationStart();
            _lagCompensationEnabled = true;
        }

        internal void DisableLagCompensation()
        {
            if (!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            ClassData.UndoHistory(this);
            OnLagCompensationEnd();
        }

        /// <summary>
        /// Enable lag compensation for player that owns this entity
        /// </summary>
        public void EnableLagCompensationForOwner()
        {
            if (InternalOwnerId.Value == EntityManager.ServerPlayerId)
                return;
            EntityManager.EnableLagCompensation(EntityManager.IsClient
                ? ClientManager.LocalPlayer
                : ServerManager.GetPlayer(InternalOwnerId));
        }

        /// <summary>
        /// Disable lag compensation for player that owns this entity
        /// </summary>
        public void DisableLagCompensationForOwner() =>
            EntityManager.DisableLagCompensation();
        
        public int GetFrameSeed() =>
            EntityManager.IsClient
                ? (EntityManager.InRollBackState ? ClientManager.RollBackTick : (ClientManager.IsExecutingRPC ? ClientManager.CurrentRPCTick : EntityManager.Tick)) 
                : (InternalOwnerId.Value == EntityManager.ServerPlayerId ? EntityManager.Tick : ServerManager.GetPlayer(InternalOwnerId).LastProcessedTick);
        
        /// <summary>
        /// Create predicted entity (like projectile) that will be replaced by server entity if prediction is successful
        /// Should be called also in rollback mode
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <param name="initMethod">Method that will be called after entity constructed</param>
        /// <returns>Created predicted local entity</returns>
        public T AddPredictedEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            T entity;
            if (EntityManager.IsServer)
            {
                entity = ServerManager.AddEntity(this, initMethod);
                entity._predictedId.Value = _localPredictedIdCounter.Value++;
                return entity;
            }

            if (IsRemoteControlled)
            {
                Logger.LogError("AddPredictedEntity called on RemoteControlled");
                return null;
            }
            
            if (EntityManager.InRollBackState)
            {
                //local counter here should be reset
                ushort potentialId = _localPredictedIdCounter.Value++;
                entity = ClientManager.FindEntityByPredictedId(ClientManager.RollBackTick, Id, potentialId) as T;
                if (entity == null)
                {
                    Logger.LogWarning("Misspredicted entity add?");
                }
                else
                {
                    //add to childs on rollback
                    Childs.Add(entity);
                }
                return entity;
            }

            entity = ClientManager.AddLocalEntity(initMethod);
            entity._predictedId.Value = _localPredictedIdCounter.Value++;
            entity._parentId.Value = new EntitySharedReference(this);
            entity.InternalOwnerId.Value = InternalOwnerId.Value;
            entity.OnParentChange(EntitySharedReference.Empty);
            entity.OnOwnerChange(EntityManager.InternalPlayerId);
            return entity;
        }
        
        /// <summary>
        /// Create predicted entity (like projectile) that will be replaced by server entity if prediction is successful
        /// In rollback state call is ignored
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <param name="targetReference">SyncVar of class that will be set to predicted entity and synchronized once confirmation will be received</param>
        /// <param name="initMethod">Method that will be called after entity constructed</param>
        /// <returns>Created predicted local entity</returns>
        public void AddPredictedEntity<T>(ref SyncVar<EntitySharedReference> targetReference, Action<T> initMethod = null) where T : EntityLogic
        {
            if (EntityManager.InRollBackState)
                return;
            
            T entity;
            if (EntityManager.IsServer)
            {
                entity = ServerManager.AddEntity(this, initMethod);
                targetReference.Value = entity;
                return;
            }

            if (IsRemoteControlled)
            {
                Logger.LogError("AddPredictedEntity called on RemoteControlled");
                return;
            }

            entity = ClientManager.AddLocalEntity(initMethod);
            entity._parentId.Value = new EntitySharedReference(this);
            entity.InternalOwnerId.Value = InternalOwnerId.Value;
            entity.OnParentChange(EntitySharedReference.Empty);
            entity.OnOwnerChange(EntityManager.InternalPlayerId);
            targetReference.Value = entity;
        }

        /// <summary>
        /// Set parent entity
        /// </summary>
        /// <param name="parentEntity">parent entity</param>
        public void SetParent(EntityLogic parentEntity)
        {
            if (EntityManager.IsClient)
                return;
            
            var id = new EntitySharedReference(parentEntity);
            if (id == _parentId.Value)
                return;
            
            EntitySharedReference oldId = _parentId;
            _parentId.Value = id;
            OnParentChange(oldId);
            
            var newParent = EntityManager.GetEntityById<EntityLogic>(_parentId)?.InternalOwnerId ?? EntityManager.ServerPlayerId;
            if (InternalOwnerId.Value != newParent)
                SetOwner(this, newParent);
        }
        
        /// <summary>
        /// Get parent entity
        /// </summary>
        /// <typeparam name="T">Type of entity</typeparam>
        /// <returns>parent entity</returns>
        public T GetParent<T>() where T : EntityLogic =>
            EntityManager.GetEntityById<T>(_parentId);
        
        /// <summary>
        /// Called when lag compensation was started for this entity
        /// </summary>
        protected virtual void OnLagCompensationStart()
        {
            
        }
        
        /// <summary>
        /// Called when lag compensation ended for this entity
        /// </summary>
        protected virtual void OnLagCompensationEnd()
        {
            
        }

        internal override void DestroyInternal()
        {
            if (IsDestroyed)
                return;

            //temporary copy childs to array because childSet can be modified inside
            var childsCopy = Childs.ToArray();
            if (childsCopy != null) //notify child entities about parent destruction
                foreach (var entityLogicRef in childsCopy)
                    EntityManager.GetEntityById<EntityLogic>(entityLogicRef).OnBeforeParentDestroy();

            base.DestroyInternal();
            if (EntityManager.IsClient && IsLocalControlled && !IsLocal)
            {
                ClientManager.RemoveOwned(this);
            }
            var parent = EntityManager.GetEntityById<EntityLogic>(_parentId);
            if (parent != null && !parent.IsDestroyed)
            {
                parent.Childs.Remove(this);
            }
            
            foreach (var entityLogicRef in Childs)
                EntityManager.GetEntityById<EntityLogic>(entityLogicRef).Destroy();
        }

        /// <summary>
        /// Called before parent destroy
        /// </summary>
        protected virtual void OnBeforeParentDestroy()
        {
            
        }
        
        private void OnOwnerChange(byte prevOwner)
        {
            if (IsLocal)
                return;
            if(prevOwner == EntityManager.InternalPlayerId)
                ClientManager.RemoveOwned(this);
            if(InternalOwnerId.Value == EntityManager.InternalPlayerId)
                ClientManager.AddOwned(this);
        }

        private void OnParentChange(EntitySharedReference oldId)
        {
            EntityManager.GetEntityById<EntityLogic>(oldId)?.Childs.Remove(this);
            EntityManager.GetEntityById<EntityLogic>(_parentId)?.Childs.Add(this);
        }

        internal static void SetOwner(EntityLogic entity, byte ownerId)
        {
            entity.InternalOwnerId.Value = ownerId;
            if (ownerId != EntityManager.ServerPlayerId)
                entity.ServerManager.GetPlayerController(ownerId)?.ForceSyncEntity(entity);
            foreach (var child in entity.Childs)
            {
                SetOwner(entity.EntityManager.GetEntityById<EntityLogic>(child), ownerId);
            }
        }
        
        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.BindOnChange(this, ref _parentId, OnParentChange);
            r.BindOnChange(this, ref InternalOwnerId, OnOwnerChange);
        }
        
        protected EntityLogic(EntityParams entityParams) : base(entityParams)
        {

        }
    }
}