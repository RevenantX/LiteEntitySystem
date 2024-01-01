using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    public struct RpcData
    {
        internal readonly int SyncableId;
        internal MethodCallDelegate ClientMethod;
        
        public RpcData(int syncableId)
        {
            SyncableId = syncableId;
            ClientMethod = null;
        }
    }
    
    public class GeneratedClassMetadata
    {
        public int FieldsCount;
        public int FieldsFlagsSize;
        public int FixedFieldsSize;
        public int PredictedSize;
        public bool HasRemoteRollbackFields;
        public int InterpolatedFieldsSize;
        public int LagCompensatedSize;
        public bool UpdateOnClient;
        public bool IsUpdateable;
        public RpcData[] RpcData;
        
        public readonly ushort BaseSyncablesCount;
        
        private struct SyncableAdditionalData
        {
            public ushort RpcOffset;
            public ExecuteFlags ExecuteFlags;
        }
        
        private SyncableAdditionalData[] _syncables;
        private List<RpcData> _rpcTempList;
        private List<SyncableAdditionalData> _syncablesTempList;

        public GeneratedClassMetadata()
        {
            _rpcTempList = new List<RpcData>();
            _syncablesTempList = new List<SyncableAdditionalData>();
        }
        
        public GeneratedClassMetadata(GeneratedClassMetadata baseClassMetadata)
        {
            FieldsCount = baseClassMetadata.FieldsCount;
            FieldsFlagsSize = baseClassMetadata.FieldsFlagsSize;
            FixedFieldsSize = baseClassMetadata.FixedFieldsSize;
            PredictedSize = baseClassMetadata.PredictedSize;
            InterpolatedFieldsSize = baseClassMetadata.InterpolatedFieldsSize;
            LagCompensatedSize = baseClassMetadata.LagCompensatedSize;
            HasRemoteRollbackFields = baseClassMetadata.HasRemoteRollbackFields;
            _rpcTempList = baseClassMetadata.RpcData != null 
                ? new List<RpcData>(baseClassMetadata.RpcData) 
                : new List<RpcData>();
            _syncablesTempList = baseClassMetadata._syncables != null
                ? new List<SyncableAdditionalData>(baseClassMetadata._syncables)
                : new List<SyncableAdditionalData>();
            BaseSyncablesCount = (ushort)_syncablesTempList.Count;
        }

        public void Init()
        {
            _syncables = _syncablesTempList.ToArray();
            _syncablesTempList = null;
            RpcData = _rpcTempList.ToArray();
            _rpcTempList = null;
            FieldsFlagsSize = (FieldsCount - 1) / 8 + 1;
        }
        
        public void AddRpc<TEntity>(ref RemoteCall rc, ExecuteFlags flags, Action<TEntity> directMethod, MethodCallDelegate clientMethod) where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                rc.CachedActionServer = e =>
                {
                    var te = (TEntity)e;
                    directMethod(te);
                    te.ServerManager.AddRemoteCall(te.Id, rpcId, flags);
                };
            else
                rc.CachedActionServer = e =>
                {
                    var te = (TEntity)e;
                    te.ServerManager.AddRemoteCall(te.Id, rpcId, flags);
                };
            if(flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                rc.CachedActionClient = e =>
                {
                    var te = (TEntity)e;
                    if (te.IsLocalControlled)
                        directMethod(te);
                };
            else
                rc.CachedActionClient = _ => { };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpc<TEntity, T>(ref RemoteCall<T> rc, ExecuteFlags flags, Action<TEntity, T> directMethod, MethodCallDelegate clientMethod) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                rc.CachedActionServer = (e, value) =>
                {
                    var te = (TEntity)e;
                    directMethod(te, value); 
                    te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                };
            else
                rc.CachedActionServer = (e, value) =>
                {
                    var te = (TEntity)e;
                    te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                };
            if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                rc.CachedActionClient = (e, value) =>
                {
                    var te = (TEntity)e;
                    if (te.IsLocalControlled) 
                        directMethod(te, value);
                };
            else
                rc.CachedActionClient = (_, _) => { };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSpan<TEntity, T>(ref RemoteCallSpan<T> rc, ExecuteFlags flags, SpanAction<TEntity, T> directMethod, MethodCallDelegate clientMethod) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            if (flags.HasFlagFast(ExecuteFlags.ExecuteOnServer))
                rc.CachedActionServer = (e, value) =>
                {
                    var te = (TEntity)e;
                    directMethod(te, value); 
                    te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                };
            else
                rc.CachedActionServer = (e, value) =>
                {
                    var te = (TEntity)e;
                    te.ServerManager.AddRemoteCall(te.Id, value, rpcId, flags);
                };
            if (flags.HasFlagFast(ExecuteFlags.ExecuteOnPrediction))
                rc.CachedActionClient = (e, value) =>
                {
                    var te = (TEntity)e;
                    if (te.IsLocalControlled) 
                        directMethod(te, value);
                };
            else
                rc.CachedActionClient = (_, _) => { };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable(ref RemoteCall rc, MethodCallDelegate clientMethod)
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            rc.CachedActionServer = s =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata()._syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable<T>(ref RemoteCall<T> rc, MethodCallDelegate clientMethod) where T : unmanaged
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            rc.CachedActionServer = (s, value) =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata()._syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable<T>(ref RemoteCallSpan<T> rc, MethodCallDelegate clientMethod) where T : unmanaged
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcTempList.Count;
            rc.CachedActionServer = (s, value) =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata()._syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcTempList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }

        public void AddField<T>(SyncFlags flags) where T : unmanaged
        {
            int size = Helpers.SizeOfStruct<T>();
            if(flags.HasFlagFast(SyncFlags.Interpolated) && !typeof(T).IsEnum)
            {
                InterpolatedFieldsSize += size;
            }
            if(flags.HasFlagFast(SyncFlags.LagCompensated))
            {
                LagCompensatedSize += size;
            }
            if(flags.HasFlagFast(SyncFlags.AlwaysRollback))
                HasRemoteRollbackFields = true;
            FixedFieldsSize += size;
            FieldsCount++;
            
            //is predicted
            if(flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
               (!flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                !flags.HasFlagFast(SyncFlags.NeverRollBack)))
            {
                PredictedSize += size;
            }
        }

        public void AddSyncableField(GeneratedClassMetadata syncableMetadata, SyncFlags fieldSyncFlags)
        {
            ExecuteFlags executeFlags;
            if (fieldSyncFlags.HasFlagFast(SyncFlags.OnlyForOwner))
                executeFlags = ExecuteFlags.SendToOwner;
            else if (fieldSyncFlags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                executeFlags = ExecuteFlags.SendToOther;
            else
                executeFlags = ExecuteFlags.SendToAll;

            var syncableData = new SyncableAdditionalData
                { RpcOffset = (ushort)_rpcTempList.Count, ExecuteFlags = executeFlags };
            foreach (var rpcData in syncableMetadata.RpcData)
                _rpcTempList.Add(new RpcData(_syncablesTempList.Count) {ClientMethod = rpcData.ClientMethod});
            _syncablesTempList.Add(syncableData);
            FixedFieldsSize += syncableMetadata.FixedFieldsSize;
            FieldsCount += syncableMetadata.FieldsCount;
            PredictedSize += syncableMetadata.PredictedSize;
        }
    }

    public static class GeneratedClassDataHandler<T> where T : InternalSyncType
    {
        public static GeneratedClassMetadata ClassMetadata;
    }
}