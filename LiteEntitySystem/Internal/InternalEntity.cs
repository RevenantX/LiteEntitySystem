using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    internal struct EntityDataHeader
    {
        public ushort Id;
        public ushort ClassId;
        public byte Version;
        public int CreationTick;
    }
    
    public class EntityComparer : IComparer<InternalEntity>
    {
        public int Compare(InternalEntity x, InternalEntity y) => x.CompareTo(y);

        public static readonly EntityComparer Instance = new();
    }
    
    public abstract class InternalEntity : InternalBaseClass, IComparable<InternalEntity>
    {
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        internal SyncVar<byte> InternalOwnerId;
        
        /// <summary>
        /// Entity class id
        /// </summary>
        public readonly ushort ClassId;
        
        /// <summary>
        /// Entity instance id
        /// </summary>
        public readonly ushort Id;

        /// <summary>
        /// Entity creation tick number that can be more than ushort
        /// </summary>
        internal readonly int CreationTick;
        
        /// <summary>
        /// Entity manager
        /// </summary>
        public readonly EntityManager EntityManager;

        /// <summary>
        /// Is entity on server
        /// </summary>
        protected internal bool IsServer => EntityManager.IsServer;
        
        /// <summary>
        /// Is entity on server
        /// </summary>
        protected bool IsClient => EntityManager.IsClient;

        /// <summary>
        /// Entity version (for id reuse)
        /// </summary>
        public readonly byte Version;

        internal EntityDataHeader DataHeader => new EntityDataHeader
        {
            Id = Id,
            ClassId = ClassId,
            CreationTick = CreationTick,
            Version = Version
        };
        
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        private SyncVar<bool> _isDestroyed;
        
        /// <summary>
        /// Is entity is destroyed
        /// </summary>
        public bool IsDestroyed => _isDestroyed;

        /// <summary>
        /// Is entity local controlled
        /// </summary>
        public bool IsLocalControlled => OwnerId == EntityManager.InternalPlayerId;

        /// <summary>
        /// Is entity remote controlled
        /// </summary>
        public bool IsRemoteControlled => OwnerId != EntityManager.InternalPlayerId;
        
        /// <summary>
        /// Is entity is controlled by server
        /// </summary>
        public bool IsServerControlled => OwnerId == EntityManager.ServerPlayerId;
        
        /// <summary>
        /// ClientEntityManager that available only on client. Will throw exception if called on server
        /// </summary>
        public ClientEntityManager ClientManager => (ClientEntityManager)EntityManager;
        
        /// <summary>
        /// ServerEntityManager that available only on server. Will throw exception if called on client
        /// </summary>
        public ServerEntityManager ServerManager => (ServerEntityManager)EntityManager;
        
        /// <summary>
        /// Owner player id
        /// ServerPlayerId - 0
        /// Singletons always controlled by server
        /// </summary>
        public byte OwnerId => InternalOwnerId;

        /// <summary>
        /// Is locally created entity
        /// </summary>
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref EntityClassData GetClassData() => ref EntityManager.ClassDataDict[ClassId];

        /// <summary>
        /// Destroy entity
        /// </summary>
        public void Destroy()
        {
            if ((EntityManager.IsClient && !IsLocal) || _isDestroyed)
                return;
            DestroyInternal();
        }
        
        private void OnDestroyChange(bool prevValue)
        {
            if (!prevValue && _isDestroyed)
            {
                _isDestroyed.Value = false;
                DestroyInternal();
            }
        }

        /// <summary>
        /// Event called on entity destroy
        /// </summary>
        protected virtual void OnDestroy()
        {

        }

        internal virtual void DestroyInternal()
        {
            if (_isDestroyed)
                return;
            _isDestroyed.Value = true;
            OnDestroy();
            EntityManager.RemoveEntity(this);
        }

        internal void SafeUpdate()
        {
            try
            {
                Update();
            }
            catch (Exception e)
            {
                Logger.LogError($"Exception in entity({Id}) update:\n{e}");
            }   
        }

        /// <summary>
        /// Fixed update. Called if entity has attribute <see cref="SetEntityFlags"/> and flag Updateable
        /// </summary>
        protected internal virtual void Update()
        {
        }

        /// <summary>
        /// Called at rollback begin after all values reset to first frame in rollback queue.
        /// </summary>
        protected internal virtual void OnRollback()
        {
            
        }

        /// <summary>
        /// Called only on <see cref="ClientEntityManager.Update"/> and if entity has attribute <see cref="SetEntityFlags"/> and flag Updateable
        /// </summary>
        protected internal virtual void VisualUpdate()
        {
            
        }

        /// <summary>
        /// Called when entity constructed
        /// </summary>
        protected internal virtual void OnConstructed()
        {
        }

        internal void RegisterRpcInternal()
        {
            ref var classData = ref GetClassData();
            
            //setup field ids for BindOnChange and pass on server this for OnChangedEvent to StateSerializer
            var onChangeTarget = EntityManager.IsServer && !IsLocal ? this : null;
            for (int i = 0; i < classData.FieldsCount; i++)
            {
                ref var field = ref classData.Fields[i];
                if (field.FieldType == FieldType.SyncVar)
                {
                    field.TypeProcessor.InitSyncVar(this, field.Offset, onChangeTarget, (ushort)i);
                }
                else
                {
                    var syncableField = RefMagic.RefFieldValue<SyncableField>(this, field.Offset);
                    field.TypeProcessor.InitSyncVar(syncableField, field.SyncableSyncVarOffset, onChangeTarget, (ushort)i);
                }
            }
          
            List<RpcFieldInfo> rpcCahce = null;
            if(classData.RemoteCallsClient == null)
            {
                rpcCahce = new List<RpcFieldInfo>();
                var rpcRegistrator = new RPCRegistrator(rpcCahce);
                RegisterRPC(ref rpcRegistrator);
                //Logger.Log($"RegisterRPCs for class: {classData.ClassId}");
            }
            //setup id for later sync calls
            for (int i = 0; i < classData.SyncableFields.Length; i++)
            {
                ref var syncFieldInfo = ref classData.SyncableFields[i];
                var syncField = RefMagic.RefFieldValue<SyncableField>(this, syncFieldInfo.Offset);
                syncField.ParentEntityInternal = this;
                if (syncFieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForOwner))
                    syncField.Flags = ExecuteFlags.SendToOwner;
                else if (syncFieldInfo.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                    syncField.Flags = ExecuteFlags.SendToOther;
                else
                    syncField.Flags = ExecuteFlags.SendToAll;
                if (classData.RemoteCallsClient != null)
                {
                    syncField.RPCOffset = syncFieldInfo.RPCOffset;
                }
                else
                {
                    syncField.RPCOffset = (ushort)rpcCahce.Count;
                    syncFieldInfo.RPCOffset = syncField.RPCOffset;
                    var syncablesRegistrator = new SyncableRPCRegistrator(syncFieldInfo.Offset, rpcCahce);
                    syncField.RegisterRPC(ref syncablesRegistrator);
                }
            }
            classData.RemoteCallsClient ??= rpcCahce.ToArray();
        }



        /// <summary>
        /// Method for registering RPCs and OnChange notifications
        /// </summary>
        /// <param name="r"></param>
        protected virtual void RegisterRPC(ref RPCRegistrator r)
        {
            r.BindOnChange(this, ref _isDestroyed, OnDestroyChange);
        }

        protected InternalEntity(EntityParams entityParams)
        {
            EntityManager = entityParams.EntityManager;
            Id = entityParams.Id;
            ClassId = entityParams.ClassId;
            Version = entityParams.Version;
            CreationTick = entityParams.CreationTime;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(InternalEntity other)
        {
            int creationTimeDiff = CreationTick - other.CreationTick;
            if (creationTimeDiff != 0)
                return creationTimeDiff;

            int versionDiff = Version - other.Version;
            if (versionDiff != 0)
                return versionDiff;
            
            //local first because mostly this is unity physics or something similar
            return (Id >= EntityManager.MaxSyncedEntityCount ? Id - ushort.MaxValue : Id) -
                   (other.Id >= EntityManager.MaxSyncedEntityCount ? other.Id - ushort.MaxValue : other.Id);
        }

        public override int GetHashCode() => Id + Version * ushort.MaxValue;

        public override string ToString() =>
            $"Entity. Id: {Id}, ClassId: {ClassId}, Version: {Version}";
    }
}