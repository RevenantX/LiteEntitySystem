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
        public bool IsRpcBound;
        public RpcData[] RpcData;

        public readonly ushort BaseFieldsCount;
        public readonly ushort BaseSyncablesCount;
        public readonly List<ushort> SyncablesRpcOffsets = new();
        
        private List<EntityFieldInfo> _fieldsTemp;
        private List<EntityFieldInfo> _lagCompensatedFieldsTemp;
        private List<RpcData> _rpcList;

        public GeneratedClassMetadata()
        {
            Fields = Array.Empty<EntityFieldInfo>();
            LagCompensatedFields = Array.Empty<EntityFieldInfo>();
            _fieldsTemp = new List<EntityFieldInfo>();
            _lagCompensatedFieldsTemp = new List<EntityFieldInfo>();
            _rpcList = new List<RpcData>();
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
            _rpcList = baseClassMetadata.RpcData != null ? new List<RpcData>(baseClassMetadata.RpcData) : new List<RpcData>();
            BaseFieldsCount = (ushort)baseClassMetadata.FieldsCount;
            BaseSyncablesCount = (ushort)baseClassMetadata.SyncablesCount;
        }

        public void Init()
        {
            if (_fieldsTemp == null)
                return;
            
            _fieldsTemp.Sort((a, b) =>
            {{
                int wa = a.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                int wb = b.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                return wb - wa;
            }});
            
            Fields = _fieldsTemp.ToArray();
            _fieldsTemp = null;
            LagCompensatedFields = _lagCompensatedFieldsTemp.ToArray();
            _lagCompensatedFieldsTemp = null;
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
        
        public void AddRpc(ref RemoteCall rc)
        {
            rc.RpcId = (ushort)_rpcList.Count;
            _rpcList.Add(new RpcData());
        }
        
        public void AddRpc<T>(ref RemoteCall<T> rc) where T : unmanaged
        {
            rc.RpcId = (ushort)_rpcList.Count;
            _rpcList.Add(new RpcData());
        }
        
        public void AddRpc<T>(ref RemoteCallSpan<T> rc) where T : unmanaged
        {
            rc.LocalId = (ushort)_rpcList.Count;
            _rpcList.Add(new RpcData());
        }

        public void AddField<T>(
            string name, 
            FieldType fieldType, 
            SyncFlags flags, 
            bool hasChangeNotification) where T : unmanaged
        {
            var fi = new EntityFieldInfo(name, fieldType, typeof(T), (ushort)_fieldsTemp.Count, 0, flags, hasChangeNotification);
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
        }

        public void AddSyncableField(GeneratedClassMetadata syncableMetadata)
        {
            ushort idOffset = (ushort)_fieldsTemp.Count;
            foreach (var fld in syncableMetadata.Fields)
            {
                _fieldsTemp.Add(new EntityFieldInfo(fld.Name, FieldType.SyncableSyncVar, fld.ActualType, (ushort)_fieldsTemp.Count, idOffset, fld.Flags, false));
            }
            SyncablesRpcOffsets.Add((ushort)_rpcList.Count);
            foreach (var rpcData in syncableMetadata.RpcData)
            {
                _rpcList.Add(new RpcData(SyncablesCount));
            }
            SyncablesCount++;
            FixedFieldsSize += syncableMetadata.FixedFieldsSize;
        }
    }

    public static class GeneratedClassDataHandler<T> where T : InternalSyncType
    {
        public static GeneratedClassMetadata ClassMetadata;
    }
}