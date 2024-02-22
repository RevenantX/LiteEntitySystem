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
        public readonly bool UpdateOnClient;
        public readonly bool IsUpdateable;
        public readonly bool IsLocalOnly;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        public RpcFieldInfo[] RemoteCallsClient;
        
        private readonly bool _isCreated;
        private readonly Type[] _baseTypes;
        
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        private static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableFieldType = typeof(SyncableField);

        private static List<Type> GetBaseTypes(Type ofType, Type until, bool includeSelf)
        {
            var baseTypes = new List<Type>();
            var baseType = ofType.BaseType;
            while (baseType != until)
            {
                baseTypes.Insert(0, baseType);
                baseType = baseType!.BaseType;
            }
            if(includeSelf)
                baseTypes.Add(ofType);
            return baseTypes;
        }

        public static bool IsRemoteCallType(Type ft)
        {
            if (ft == typeof(RemoteCall))
                return true;
            if (!ft.IsGenericType)
                return false;
            var genericTypeDef = ft.GetGenericTypeDefinition();
            return genericTypeDef == typeof(RemoteCall<>) || genericTypeDef == typeof(RemoteCallSpan<>);
        }

        public EntityClassData(ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            HasRemoteRollbackFields = false;
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            LagCompensatedCount = 0;
            InterpolatedCount = 0;
            InterpolatedFieldsSize = 0;
            RemoteCallsClient = null;

            ClassId = typeInfo.ClassId;

            var updateAttribute = entType.GetCustomAttribute<UpdateableEntity>();
            if (updateAttribute != null)
            {
                IsUpdateable = true;
                UpdateOnClient = updateAttribute.UpdateOnClient;
            }
            else
            {
                IsUpdateable = false;
                UpdateOnClient = false;
            }

            IsLocalOnly = entType.GetCustomAttribute<LocalOnly>() != null;
            EntityConstructor = typeInfo.Constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;

            var baseTypes = GetBaseTypes(entType, InternalEntityType, false);
            _baseTypes = baseTypes.ToArray();
            BaseIds = new ushort[baseTypes.Count];
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<SyncableFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();

            //add here to baseTypes to add fields
            baseTypes.Insert(0, typeof(InternalEntity));
            baseTypes.Add(entType);

            const BindingFlags bindingFlags = BindingFlags.Instance |
                                              BindingFlags.Public |
                                              BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly |
                                              BindingFlags.Static;
            
            foreach (var baseType in baseTypes)
            {
                //cache fields
                foreach (var field in baseType.GetFields(bindingFlags))
                {
                    var ft = field.FieldType;
                    if(IsRemoteCallType(ft) && !field.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {field.Name})");
                    
                    if(field.IsStatic)
                        continue;
                    
                    var syncVarFieldAttribute = field.GetCustomAttribute<SyncVarFlags>();
                    var syncVarClassAttribute = baseType.GetCustomAttribute<SyncVarFlags>();
                    var syncFlags = syncVarFieldAttribute?.Flags
                                 ?? syncVarClassAttribute?.Flags
                                 ?? SyncFlags.None;
                    int offset = Utils.GetFieldOffset(field);
                    
                    //syncvars
                    if (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        ft = ft.GetGenericArguments()[0];
                        if (ft.IsEnum)
                            ft = ft.GetEnumUnderlyingType();

                        if (!ValueProcessors.RegisteredProcessors.TryGetValue(ft, out var valueTypeProcessor))
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
                        var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}", valueTypeProcessor, offset, syncFlags);
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
                        foreach (var syncableType in GetBaseTypes(ft, SyncableFieldType, true))
                        {
                            //syncable fields
                            foreach (var syncableField in syncableType.GetFields(bindingFlags))
                            {
                                var syncableFieldType = syncableField.FieldType;
                                if(IsRemoteCallType(syncableFieldType) && !syncableField.IsStatic)
                                    throw new Exception($"RemoteCalls should be static! (Class: {syncableType} Field: {syncableField.Name})");
                                
                                if (!syncableFieldType.IsValueType || 
                                    !syncableFieldType.IsGenericType || 
                                    syncableFieldType.GetGenericTypeDefinition() != typeof(SyncVar<>) ||
                                    syncableField.IsStatic) 
                                    continue;

                                syncableFieldType = syncableFieldType.GetGenericArguments()[0];
                                if (syncableFieldType.IsEnum)
                                    syncableFieldType = syncableFieldType.GetEnumUnderlyingType();

                                if (!ValueProcessors.RegisteredProcessors.TryGetValue(syncableFieldType, out var valueTypeProcessor))
                                {
                                    Logger.LogError($"Unregistered field type: {syncableFieldType}");
                                    continue;
                                }
                                int syncvarOffset = Utils.GetFieldOffset(syncableField);
                                var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}:{syncableField.Name}", valueTypeProcessor, offset, syncvarOffset, syncFlags);
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