using System;
using System.Collections.Generic;
using System.Reflection;

namespace LiteEntitySystem.Internal
{
    internal struct SyncableFieldInfo
    {
        public readonly int Offset;
        public readonly SyncFlags Flags;
        public ushort RPCOffset;

        public SyncableFieldInfo(int offset, SyncFlags executeFlags)
        {
            Offset = offset;
            Flags = executeFlags;
            RPCOffset = ushort.MaxValue;
        }
    }
    
    internal readonly struct RpcFieldInfo
    {
        public readonly int SyncableOffset;
        public readonly MethodCallDelegate Method;

        public RpcFieldInfo(MethodCallDelegate method)
        {
            SyncableOffset = -1;
            Method = method;
        }
        
        public RpcFieldInfo(int syncableOffset, MethodCallDelegate method)
        {
            SyncableOffset = syncableOffset;
            Method = method;
        }
    }
    
    internal struct EntityClassData
    {
        public static readonly EntityClassData Empty = new EntityClassData();
        
        public readonly ushort ClassId;
        public readonly ushort FilterId;
        public readonly bool IsSingleton;
        public readonly ushort[] BaseIds;
        public readonly int FieldsCount;
        public readonly int FieldsFlagsSize;
        public readonly int FixedFieldsSize;
        public readonly int PredictedSize;
        public readonly bool HasRemoteRollbackFields;
        public readonly EntityFieldInfo[] Fields;
        public readonly SyncableFieldInfo[] SyncableFields;
        public readonly int InterpolatedFieldsSize;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;
        public readonly int LagCompensatedCount;
        public readonly EntityFlags Flags;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        
        public RpcFieldInfo[] RemoteCallsClient;
        
        private readonly bool _isCreated;
        private readonly Type[] _baseTypes;
        private readonly Queue<byte[]> _dataCache;
        
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        private static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableFieldType = typeof(SyncableField);

        private int _dataCacheSize;
        private int _historySize;
        private int _maxHistoryCount;
        private int _historyStart;

        public Span<byte> ClientInterpolatedPrevData(InternalEntity e) => new (e.IOBuffer, 0, InterpolatedFieldsSize);
        public Span<byte> ClientInterpolatedNextData(InternalEntity e) => new (e.IOBuffer, InterpolatedFieldsSize, InterpolatedFieldsSize);
        public Span<byte> ClientPredictedData(InternalEntity e) => new (e.IOBuffer, InterpolatedFieldsSize*2, PredictedSize);
        
        public unsafe void WriteHistory(EntityLogic e, ushort tick)
        {
            int historyOffset = ((tick % _maxHistoryCount)+1)*LagCompensatedSize;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.WriteTo(e, field.Offset, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }

        public unsafe void LoadHistroy(NetPlayer player, EntityLogic e)
        {
            int historyAOffset = ((player.StateATick % _maxHistoryCount)+1)*LagCompensatedSize;
            int historyBOffset = ((player.StateBTick % _maxHistoryCount)+1)*LagCompensatedSize;
            int historyCurrent = 0;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.LoadHistory(
                        e, 
                        field.Offset,
                        history + historyCurrent,
                        history + historyAOffset,
                        history + historyBOffset,
                        player.LerpTime);
                    historyAOffset += field.IntSize;
                    historyBOffset += field.IntSize;
                    historyCurrent += field.IntSize;
                }
            }
        }

        public unsafe void UndoHistory(EntityLogic e)
        {
            int historyOffset = 0;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.SetFrom(e, field.Offset, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }
        
        public void AllocateDataCache(InternalEntity entity)
        {
            if (_maxHistoryCount == 0)
            {
                _maxHistoryCount = (byte)entity.EntityManager.MaxHistorySize;
                _historySize = (_maxHistoryCount + 1) * LagCompensatedSize;
                _dataCacheSize = entity.IsServer ? _historySize : (InterpolatedFieldsSize * 2 + PredictedSize + _historySize);
                _historyStart = entity.IsServer ? 0 : (InterpolatedFieldsSize * 2 + PredictedSize);
            }
            entity.IOBuffer = _dataCache.Count > 0 ? _dataCache.Dequeue() : new byte[_dataCacheSize];
        }

        public void ReleaseDataCache(InternalEntity entity)
        {
            if (entity.IOBuffer != null && entity.IOBuffer.Length == _dataCacheSize)
            {
                _dataCache.Enqueue(entity.IOBuffer);
                Array.Clear(entity.IOBuffer, 0, _dataCacheSize);
                entity.IOBuffer = null;
            }
        }

        public EntityClassData(ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            _dataCache = new Queue<byte[]>();
            HasRemoteRollbackFields = false;
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            LagCompensatedCount = 0;
            InterpolatedCount = 0;
            InterpolatedFieldsSize = 0;
            
            _dataCacheSize = 0;
            _maxHistoryCount = 0;
            _historySize = 0;
            _historyStart = 0;
            
            RemoteCallsClient = null;

            ClassId = typeInfo.ClassId;
            Flags = 0;
            
            EntityConstructor = typeInfo.Constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;
            
            _baseTypes = Utils.GetBaseTypes(entType, InternalEntityType, false).ToArray();
            BaseIds = new ushort[_baseTypes.Length];
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<SyncableFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();
            
            var allTypesStack = Utils.GetBaseTypes(entType, typeof(object), true);
            while(allTypesStack.Count > 0)
            {
                var baseType = allTypesStack.Pop();
                
                var setFlagsAttribute = baseType.GetCustomAttribute<EntityFlagsAttribute>();
                Flags |= setFlagsAttribute != null ? setFlagsAttribute.Flags : 0;
                
                //cache fields
                foreach (var field in Utils.GetProcessedFields(baseType))
                {
                    var ft = field.FieldType;
                    if(Utils.IsRemoteCallType(ft) && !field.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {field.Name})");
                    
                    if(field.IsStatic)
                        continue;
                    
                    var syncVarFlags = field.GetCustomAttribute<SyncVarFlags>() ?? baseType.GetCustomAttribute<SyncVarFlags>();
                    var syncFlags = syncVarFlags?.Flags ?? SyncFlags.None;
                    int offset = Utils.GetFieldOffset(field);
                    
                    //syncvars
                    if (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        ft = ft.GetGenericArguments()[0];
                        if (ft.IsEnum)
                            ft = ft.GetEnumUnderlyingType();

                        if (!ValueTypeProcessor.Registered.TryGetValue(ft, out var valueTypeProcessor))
                        {
                            Logger.LogError($"Unregistered field type: {ft}");
                            continue;
                        }
                        int fieldSize = valueTypeProcessor.Size;
                        if (syncFlags.HasFlagFast(SyncFlags.Interpolated) && !ft.IsEnum)
                        {
                            InterpolatedFieldsSize += fieldSize;
                            InterpolatedCount++;
                        }
                        var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}", valueTypeProcessor, offset, syncVarFlags);
                        if (syncFlags.HasFlagFast(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(fieldInfo);
                            LagCompensatedSize += fieldSize;
                        }
                        if (syncFlags.HasFlagFast(SyncFlags.AlwaysRollback))
                            HasRemoteRollbackFields = true;

                        if (fieldInfo.IsPredicted)
                            PredictedSize += fieldSize;

                        fields.Add(fieldInfo);
                        FixedFieldsSize += fieldSize;
                    }
                    else if (ft.IsSubclassOf(SyncableFieldType))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception($"Syncable fields should be readonly! (Class: {entType} Field: {field.Name})");
                        
                        syncableFields.Add(new SyncableFieldInfo(offset, syncFlags));
                        var syncableFieldTypesWithBase = Utils.GetBaseTypes(ft, SyncableFieldType, true);
                        while(syncableFieldTypesWithBase.Count > 0)
                        {
                            var syncableType = syncableFieldTypesWithBase.Pop();
                            //syncable fields
                            foreach (var syncableField in Utils.GetProcessedFields(syncableType))
                            {
                                var syncableFieldType = syncableField.FieldType;
                                if(Utils.IsRemoteCallType(syncableFieldType) && !syncableField.IsStatic)
                                    throw new Exception($"RemoteCalls should be static! (Class: {syncableType} Field: {syncableField.Name})");
                                
                                if (!syncableFieldType.IsValueType || 
                                    !syncableFieldType.IsGenericType || 
                                    syncableFieldType.GetGenericTypeDefinition() != typeof(SyncVar<>) ||
                                    syncableField.IsStatic) 
                                    continue;

                                syncableFieldType = syncableFieldType.GetGenericArguments()[0];
                                if (syncableFieldType.IsEnum)
                                    syncableFieldType = syncableFieldType.GetEnumUnderlyingType();

                                if (!ValueTypeProcessor.Registered.TryGetValue(syncableFieldType, out var valueTypeProcessor))
                                {
                                    Logger.LogError($"Unregistered field type: {syncableFieldType}");
                                    continue;
                                }
                                int syncvarOffset = Utils.GetFieldOffset(syncableField);
                                var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}:{syncableField.Name}", valueTypeProcessor, offset, syncvarOffset, syncVarFlags);
                                fields.Add(fieldInfo);
                                FixedFieldsSize += fieldInfo.IntSize;
                                if (fieldInfo.IsPredicted)
                                    PredictedSize += fieldInfo.IntSize;
                            }
                        }
                    }
                }
            }
            
            //sort by placing interpolated first
            fields.Sort((a, b) =>
            {
                int wa = a.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                int wb = b.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                return wb - wa;
            });
            Fields = fields.ToArray();
            SyncableFields = syncableFields.ToArray();
            FieldsCount = Fields.Length;
            FieldsFlagsSize = (FieldsCount-1) / 8 + 1;
            LagCompensatedFields = lagCompensatedFields.ToArray();
            LagCompensatedCount = LagCompensatedFields.Length;

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
            _isCreated = true;
        }

        public void PrepareBaseTypes(Dictionary<Type, ushort> registeredTypeIds, ref ushort singletonCount, ref ushort filterCount)
        {
            if (!_isCreated)
                return;
            for (int i = 0; i < BaseIds.Length; i++)
            {
                if (!registeredTypeIds.TryGetValue(_baseTypes[i], out BaseIds[i]))
                {
                    BaseIds[i] = IsSingleton
                        ? singletonCount++
                        : filterCount++;
                    registeredTypeIds.Add(_baseTypes[i], BaseIds[i]);
                }
                //Logger.Log($"Base type of {classData.ClassId} - {baseTypes[i]}");
            }
        }
    }

    // ReSharper disable once UnusedTypeParameter
    internal static class EntityClassInfo<T>
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static ushort ClassId;
    }
}