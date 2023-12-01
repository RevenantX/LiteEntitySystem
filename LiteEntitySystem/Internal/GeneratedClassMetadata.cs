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

    public struct SyncableAdditionalData
    {
        public ushort RpcOffset;
        public ExecuteFlags ExecuteFlags;
    }
    
    public class GeneratedClassMetadata
    {
        public int FieldsCount;
        public int SyncablesCount;
        public int FieldsFlagsSize;
        public int FixedFieldsSize;
        public int PredictedSize;
        public bool HasRemoteRollbackFields;
        public EntityFieldInfo[] Fields;
        public int InterpolatedFieldsSize;
        public int InterpolatedCount;
        public EntityFieldInfo[] LagCompensatedFields;
        public int LagCompensatedSize;
        public int LagCompensatedCount;
        public bool UpdateOnClient;
        public bool IsUpdateable;
        public RpcData[] RpcData;
        public SyncableAdditionalData[] Syncables;

        public ushort FieldIdCounter;
        public readonly ushort BaseFieldIdCounter;
        public readonly ushort BaseSyncablesCount;
        
        private List<EntityFieldInfo> _fieldsTemp;
        private List<EntityFieldInfo> _lagCompensatedFieldsTemp;
        private List<RpcData> _rpcList;
        private List<SyncableAdditionalData> _syncables;

        public GeneratedClassMetadata()
        {
            Fields = Array.Empty<EntityFieldInfo>();
            LagCompensatedFields = Array.Empty<EntityFieldInfo>();
            _fieldsTemp = new List<EntityFieldInfo>();
            _lagCompensatedFieldsTemp = new List<EntityFieldInfo>();
            _rpcList = new List<RpcData>();
            _syncables = new List<SyncableAdditionalData>();
        }
        
        public GeneratedClassMetadata(GeneratedClassMetadata baseClassMetadata)
        {
            SyncablesCount = baseClassMetadata.SyncablesCount;
            FieldsFlagsSize = baseClassMetadata.FieldsFlagsSize;
            FixedFieldsSize = baseClassMetadata.FixedFieldsSize;
            PredictedSize = baseClassMetadata.PredictedSize;
            InterpolatedFieldsSize = baseClassMetadata.InterpolatedFieldsSize;
            InterpolatedCount = baseClassMetadata.InterpolatedCount;
            LagCompensatedSize = baseClassMetadata.LagCompensatedSize;
            HasRemoteRollbackFields = baseClassMetadata.HasRemoteRollbackFields;
            _fieldsTemp = new List<EntityFieldInfo>(baseClassMetadata.Fields);
            _lagCompensatedFieldsTemp = new List<EntityFieldInfo>(baseClassMetadata.LagCompensatedFields);
            _rpcList = baseClassMetadata.RpcData != null 
                ? new List<RpcData>(baseClassMetadata.RpcData) 
                : new List<RpcData>();
            _syncables = baseClassMetadata.Syncables != null
                ? new List<SyncableAdditionalData>(baseClassMetadata.Syncables)
                : new List<SyncableAdditionalData>();
            BaseSyncablesCount = (ushort)baseClassMetadata.SyncablesCount;
            FieldIdCounter = baseClassMetadata.FieldIdCounter;
            BaseFieldIdCounter = FieldIdCounter;
        }

        public void Init()
        {
            if (_fieldsTemp == null)
                return;
            
            _fieldsTemp.Sort((a, b) =>
            {
                int wa = a.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                int wb = b.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                return wb - wa;
            });
            
            Fields = _fieldsTemp.ToArray();
            _fieldsTemp = null;
            LagCompensatedFields = _lagCompensatedFieldsTemp.ToArray();
            _lagCompensatedFieldsTemp = null;
            Syncables = _syncables.ToArray();
            _syncables = null;
            RpcData = _rpcList.ToArray();
            _rpcList = null;
            FieldsCount = Fields.Length;
            LagCompensatedCount = LagCompensatedFields.Length;
            FieldsFlagsSize = (FieldsCount - 1) / 8 + 1;
            
            int fixedOffset = 0;
            int predictedOffset = 0;
            for (int i = 0; i < Fields.Length; i++)
            {
                ref var field = ref Fields[i];
                field.FixedOffset = fixedOffset;
                fixedOffset += field.IntSize;
                if (field.IsPredicted)
                {
                    field.PredictedOffset = predictedOffset;
                    predictedOffset += field.IntSize;
                }
                else
                {
                    field.PredictedOffset = -1;
                }
            }
        }
        
        public void AddRpc<TEntity>(ref RemoteCall rc, ExecuteFlags flags, Action<TEntity> directMethod, MethodCallDelegate clientMethod) where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
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
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpc<TEntity, T>(ref RemoteCall<T> rc, ExecuteFlags flags, Action<TEntity, T> directMethod, MethodCallDelegate clientMethod) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
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
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSpan<TEntity, T>(ref RemoteCallSpan<T> rc, ExecuteFlags flags, SpanAction<TEntity, T> directMethod, MethodCallDelegate clientMethod) where T : unmanaged where TEntity : InternalEntity
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
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
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable(ref RemoteCall rc, MethodCallDelegate clientMethod)
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
            rc.CachedActionServer = s =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata().Syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable<T>(ref RemoteCall<T> rc, MethodCallDelegate clientMethod) where T : unmanaged
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
            rc.CachedActionServer = (s, value) =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata().Syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }
        
        public void AddRpcSyncable<T>(ref RemoteCallSpan<T> rc, MethodCallDelegate clientMethod) where T : unmanaged
        {
            ushort rpcId = rc.LocalId = (ushort)_rpcList.Count;
            rc.CachedActionServer = (s, value) =>
            {
                var sf = (SyncableField)s;
                var syncData = sf.ParentEntity.GetClassMetadata().Syncables[sf.SyncableId];
                sf.ParentEntity.ServerManager.AddRemoteCall(sf.ParentEntity.Id, value, (ushort)(syncData.RpcOffset + rpcId), syncData.ExecuteFlags);
            };
            _rpcList.Add(new RpcData(-1){ClientMethod = clientMethod});
        }

        public void AddField<T>(string name, FieldType fieldType, SyncFlags flags, bool hasChangeNotification) where T : unmanaged
        {
            var fi = new EntityFieldInfo(name, fieldType, typeof(T), FieldIdCounter, 0, flags, hasChangeNotification);
            int size = Helpers.SizeOfStruct<T>();
            if(flags.HasFlagFast(SyncFlags.Interpolated) && !typeof(T).IsEnum)
            {
                InterpolatedFieldsSize += size;
                InterpolatedCount++;
            }
            if(flags.HasFlagFast(SyncFlags.LagCompensated))
            {
                _lagCompensatedFieldsTemp.Add(fi);
                LagCompensatedSize += size;
            }
            if(flags.HasFlagFast(SyncFlags.AlwaysRollback))
                HasRemoteRollbackFields = true;
            if(fi.IsPredicted)
                PredictedSize += size;
            FixedFieldsSize += size;
            _fieldsTemp.Add(fi);
            FieldIdCounter++;
        }

        public void AddSyncableField(GeneratedClassMetadata syncableMetadata, SyncFlags fieldSyncFlags)
        {
            foreach (var fld in syncableMetadata.Fields)
            {
                _fieldsTemp.Add(new EntityFieldInfo(fld.Name, FieldType.SyncableSyncVar, fld.ActualType, FieldIdCounter, fld.Id, fld.Flags, false));
            }

            ExecuteFlags executeFlags;
            if (fieldSyncFlags.HasFlagFast(SyncFlags.OnlyForOwner))
                executeFlags = ExecuteFlags.SendToOwner;
            else if (fieldSyncFlags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                executeFlags = ExecuteFlags.SendToOther;
            else
                executeFlags = ExecuteFlags.SendToAll;
            
            _syncables.Add(new SyncableAdditionalData { RpcOffset = (ushort)_rpcList.Count, ExecuteFlags = executeFlags});
            foreach (var rpcData in syncableMetadata.RpcData)
            {
                _rpcList.Add(new RpcData(SyncablesCount) {ClientMethod = rpcData.ClientMethod});
            }
            SyncablesCount++;
            FixedFieldsSize += syncableMetadata.FixedFieldsSize;
            FieldIdCounter++;
        }
    }

    public static class GeneratedClassDataHandler<T> where T : InternalSyncType
    {
        public static GeneratedClassMetadata ClassMetadata;
    }
}