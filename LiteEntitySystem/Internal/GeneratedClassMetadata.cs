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

        public int BaseSyncablesCount;
        public int BaseFieldsCount;

        public GeneratedClassMetadata()
        {
            Fields = Array.Empty<EntityFieldInfo>();
            LagCompensatedFields = Array.Empty<EntityFieldInfo>();
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

            BaseFieldsCount = baseClassMetadata.FieldsCount;
            BaseSyncablesCount = baseClassMetadata.SyncablesCount;
        }

        public void AddField<T>(
            EntityFieldInfo fieldInfo, 
            List<EntityFieldInfo> fields,
            List<EntityFieldInfo> lagCompensatedFields) where T : unmanaged
        {
            int size = Helpers.SizeOfStruct<T>();
            if(fieldInfo.Flags.HasFlagFast(SyncFlags.Interpolated) && !typeof(T).IsEnum)
            {
                InterpolatedFieldsSize += size;
                InterpolatedCount++;
            }
            if(fieldInfo.Flags.HasFlagFast(SyncFlags.LagCompensated))
            {
                lagCompensatedFields.Add(fieldInfo);
                LagCompensatedSize += size;
            }
            if(fieldInfo.Flags.HasFlagFast(SyncFlags.AlwaysRollback))
                HasRemoteRollbackFields = true;
            if(fieldInfo.IsPredicted)
                PredictedSize += size;
            FixedFieldsSize += size;
            fields.Add(fieldInfo);
        }

        public void AddSyncableField(List<EntityFieldInfo> fields, GeneratedClassMetadata syncableMetadata)
        {
            fields.AddRange(syncableMetadata.Fields);
            //TODO: replace id with
            SyncablesCount++;
            FixedFieldsSize += syncableMetadata.FixedFieldsSize;
        }
    }

    public static class GeneratedClassDataHandler<T> where T : InternalSyncType
    {
        public static GeneratedClassMetadata ClassMetadata;
    }
}