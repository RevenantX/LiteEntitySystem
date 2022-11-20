using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteEntitySystem.Internal
{
    internal struct SyncableFieldInfo
    {
        public readonly int Offset;
        public readonly int ParentOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly UIntPtr PtrSize;

        public int FixedOffset;

        public SyncableFieldInfo(int parentOffset, int offset, int size)
        {
            ParentOffset = parentOffset;
            Offset = offset;
            Size = (uint)size;
            IntSize = size;
            PtrSize = (UIntPtr)Size;
            FixedOffset = 0;
        }
    }
    
    internal readonly struct EntityClassData
    {
        public readonly bool IsCreated;
        
        public readonly ushort ClassId;
        public readonly ushort FilterId;
        public readonly bool IsSingleton;
        public readonly ushort[] BaseIds;
        public readonly int FieldsCount;
        public readonly int FieldsFlagsSize;
        public readonly int FixedFieldsSize;
        public readonly int PredictedSize;
        public readonly bool HasRemotePredictedFields;
        public readonly EntityFieldInfo[] Fields;
        public readonly EntityFieldInfo[] SyncableFields;
        public readonly int InterpolatedFieldsSize;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;

        public readonly bool UpdateOnClient;
        public readonly bool IsUpdateable;
        public readonly bool IsLocalOnly;
        public readonly Type[] BaseTypes;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        public readonly Dictionary<MethodInfo, RemoteCall> RemoteCalls;
        public readonly MethodCallDelegate[] RemoteCallsClient;
        public readonly MethodCallDelegate[] SyncableRemoteCallsClient;
        public readonly Dictionary<MethodInfo, SyncableRemoteCall> SyncableRemoteCalls;

        private static readonly int NativeFieldOffset;
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        private static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableType = typeof(SyncableField);

        private class TestOffset
        {
#pragma warning disable CS0414
            public readonly uint TestValue = 0xDEADBEEF;
#pragma warning restore CS0414
        }
        
        static unsafe EntityClassData()
        {
            //check field offset
            int monoOffset = 3 * IntPtr.Size;
            int dotnetOffset = 4 + IntPtr.Size;

            var field = typeof(TestOffset).GetField("TestValue");
            int monoFieldOffset = Marshal.ReadInt32(field.FieldHandle.Value + monoOffset) & 0xFFFFFF;
            int dotnetFieldOffset = Marshal.ReadInt32(field.FieldHandle.Value + dotnetOffset) & 0xFFFFFF;

            TestOffset to = new TestOffset();
            byte* rawData = (byte*)Unsafe.As<TestOffset, IntPtr>(ref to);
            if (*(uint*)(rawData + monoFieldOffset) == 0xDEADBEEF)
                NativeFieldOffset = monoOffset;
            else if (*(uint*)(rawData + dotnetFieldOffset) == 0xDEADBEEF)
                NativeFieldOffset = dotnetOffset;
            else
                Logger.Log("Unknown native field offset");
        }
        
        private static MethodCallDelegate GetOnSyncDelegate(Type classType, Type valueType, string methodName)
        {
            if (string.IsNullOrEmpty(methodName))
                return null;
            
            var method = classType.GetMethod(
                methodName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly |
                BindingFlags.NonPublic);
            if (method == null)
            {
                Logger.LogError($"Method: {methodName} not found in {classType}");
                return null;
            }

            return GetOnSyncDelegate(classType, valueType, method);
        }

        private static MethodCallDelegate GetOnSyncDelegate(Type classType, Type valueType, MethodInfo method)
        {
            try
            {
                if (valueType == null)
                {
                    return (MethodCallDelegate)MethodCallGenerator.GenerateNoParamsMethod.MakeGenericMethod(classType)
                        .Invoke(null, new object[] { method });
                }
                else
                {
                    var genericMethod = MethodCallGenerator.GenerateMethod.MakeGenericMethod(
                        classType, 
                        valueType.IsReadonlySpan() ? valueType.GenericTypeArguments[0] : valueType);
                    
                    return (MethodCallDelegate)genericMethod.Invoke(null, new object[] { method, valueType.IsReadonlySpan() });
                }
            }
            catch(Exception e)
            {
                throw new Exception($"{classType.Name}.{method.Name} has something wrong with types: {e}");
            }
        }

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

        private static int GetTypeSize(Type type)
        {
            return (int)typeof(Unsafe).GetMethod("SizeOf").MakeGenericMethod(type).Invoke(null, null);
        }

        public EntityClassData(ushort filterId, Type entType, ushort classId, EntityConstructor<InternalEntity> constructor)
        {
            HasRemotePredictedFields = false;
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            InterpolatedCount = 0;
            InterpolatedFieldsSize = 0;
            RemoteCalls = new Dictionary<MethodInfo, RemoteCall>();
            RemoteCallsClient = new MethodCallDelegate[255];
            SyncableRemoteCallsClient = new MethodCallDelegate[255];
            SyncableRemoteCalls = new Dictionary<MethodInfo, SyncableRemoteCall>();
            
            ClassId = classId;

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
            EntityConstructor = constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;

            var baseTypes = GetBaseTypes(entType, InternalEntityType, false);
            BaseTypes = baseTypes.ToArray();
            BaseIds = new ushort[baseTypes.Count];
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<EntityFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();

            //add here to baseTypes to add fields
            baseTypes.Insert(0, typeof(InternalEntity));
            baseTypes.Add(entType);

            const BindingFlags bindingFlags = BindingFlags.Instance |
                                              BindingFlags.Public |
                                              BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly;

            byte rpcIndex = 0;
            byte syncableRpcIndex = 0;
            foreach (var baseType in baseTypes)
            {
                //cache rpcs
                foreach (var method in baseType.GetMethods(bindingFlags))
                {
                    var remoteCallAttribute = method.GetCustomAttribute<RemoteCall>();
                    if(remoteCallAttribute == null)
                        continue;

                    var methodParams = method.GetParameters();
                    var parameterType = methodParams.Length > 0 ? methodParams[0].ParameterType : null;
                    if (remoteCallAttribute.Id == byte.MaxValue)
                    {
                        remoteCallAttribute.Id = rpcIndex++;
                        if (parameterType != null)
                        {
                            remoteCallAttribute.IsArray = parameterType.IsArray;
                            remoteCallAttribute.DataSize = GetTypeSize(parameterType.HasElementType ? parameterType.GetElementType() : parameterType);
                        }
                        if (rpcIndex == byte.MaxValue)
                            throw new Exception("254 is max RemoteCall methods");
                    }
                    RemoteCalls.Add(method, remoteCallAttribute);
                    RemoteCallsClient[remoteCallAttribute.Id] = GetOnSyncDelegate(baseType, parameterType, method);
                }
                
                //cache fields
                foreach (var field in baseType.GetFields(bindingFlags))
                {
                    var syncVarAttribute = field.GetCustomAttribute<SyncVar>();
                    if(syncVarAttribute == null)
                        continue;
                    
                    var ft = field.FieldType;
                    if (ft.IsArray)
                    {
                        Logger.LogError($"SyncVar cannot be array! {field.Name} - {ft}");
                        continue;
                    }
                    int offset = Marshal.ReadInt32(field.FieldHandle.Value + NativeFieldOffset) & 0xFFFFFF;

                    if (ft.IsValueType)
                    {
                        if (ft.IsEnum)
                            ft = ft.GetEnumUnderlyingType();
                        
                        int fieldSize = GetTypeSize(ft);
                        InterpolatorDelegate interpolator = null;
                        
                        if (syncVarAttribute.Flags.HasFlagFast(SyncFlags.Interpolated) && !ft.IsArray && !ft.IsEnum)
                        {
                            if (!Interpolation.Methods.TryGetValue(ft, out interpolator))
                                throw new ArgumentException($"No info how to interpolate: {ft}");
                            InterpolatedFieldsSize += fieldSize;
                            InterpolatedCount++;
                        }

                        MethodCallDelegate onSyncMethod = GetOnSyncDelegate(baseType, ft, syncVarAttribute.MethodName);
                        var fieldInfo = new EntityFieldInfo(onSyncMethod, interpolator, offset, fieldSize, syncVarAttribute.Flags, ft == typeof(EntitySharedReference));
                        if (syncVarAttribute.Flags.HasFlagFast(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(fieldInfo);
                            LagCompensatedSize += fieldSize;
                        }
                        
                        if (fieldInfo.IsPredicted)
                        {
                            PredictedSize += fieldSize;
                        }

                        if (syncVarAttribute.Flags.HasFlagFast(SyncFlags.RemotePredicted))
                        {
                            HasRemotePredictedFields = true;
                        }

                        fields.Add(fieldInfo);
                        FixedFieldsSize += fieldSize;
                    }
                    else if (ft.IsSubclassOf(SyncableType))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception("Syncable fields should be readonly!");

                        syncableFields.Add(new EntityFieldInfo(offset, syncVarAttribute.Flags));
                        foreach (var syncableType in GetBaseTypes(ft, typeof(SyncableField), true))
                        {
                            //syncable fields
                            foreach (var syncableField in syncableType.GetFields(bindingFlags))
                            {
                                var syncableFieldAttribute = syncableField.GetCustomAttribute<SyncableSyncVar>();
                                if (syncableFieldAttribute == null)
                                    continue;
                                var syncableFieldType = syncableField.FieldType;
                                if (syncableFieldType.IsValueType)
                                {
                                    if (syncableFieldType.IsEnum)
                                        syncableFieldType = syncableFieldType.GetEnumUnderlyingType();
                                    int syncvarOffset = Marshal.ReadInt32(syncableField.FieldHandle.Value + NativeFieldOffset) & 0xFFFFFF;
                                    int size = GetTypeSize(syncableFieldType);
                                    var fieldInfo = new EntityFieldInfo(offset, syncvarOffset, size,
                                        syncVarAttribute.Flags);
                                    fields.Add(fieldInfo);
                                    FixedFieldsSize += size;
                                    if (fieldInfo.IsPredicted)
                                    {
                                        PredictedSize += size;
                                    }
                                }
                                else
                                {
                                    throw new Exception("Syncronized fields in SyncableField should be ValueType only!");
                                }
                            }

                            //syncable rpcs
                            foreach (var method in syncableType.GetMethods(bindingFlags))
                            {
                                var rcAttribute = method.GetCustomAttribute<SyncableRemoteCall>();
                                if(rcAttribute == null)
                                    continue;

                                var parameters = method.GetParameters();
                                var parameterType = parameters.Length == 0 ? null : parameters[0].ParameterType;
                                if (rcAttribute.Id == byte.MaxValue)
                                {
                                    rcAttribute.Id = syncableRpcIndex++;
                                    if (syncableRpcIndex == byte.MaxValue)
                                        throw new Exception("254 is max RemoteCall methods");
                                    
                                    rcAttribute.DataSize = parameterType == null 
                                        ? 0 
                                        : GetTypeSize(parameterType.HasElementType ? parameterType.GetElementType() : parameterType);
                                }
                                
                                SyncableRemoteCalls[method] = rcAttribute;
                                SyncableRemoteCallsClient[rcAttribute.Id] = GetOnSyncDelegate(syncableType, parameterType, method);
                            }
                        }
                    }
                    else
                    {
                        Logger.LogError($"Unsupported SyncVar: {field.Name} - {ft}");
                    }
                }
            }
            
            //sort by placing interpolated first
            fields.Sort((a, b) =>
            {
                int wa = a.Interpolator != null ? 1 : 0;
                int wb = b.Interpolator != null ? 1 : 0;
                return wb - wa;
            });
            Fields = fields.ToArray();
            SyncableFields = syncableFields.ToArray();
            FieldsCount = Fields.Length;
            FieldsFlagsSize = (FieldsCount-1) / 8 + 1;
            LagCompensatedFields = lagCompensatedFields.ToArray();
            
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

            IsCreated = true;
        }

        public void PrepareBaseTypes(Dictionary<Type, ushort> registeredTypeIds, ref ushort singletonCount, ref ushort filterCount)
        {
            if (!IsCreated)
                return;
            for (int i = 0; i < BaseIds.Length; i++)
            {
                if (!registeredTypeIds.TryGetValue(BaseTypes[i], out BaseIds[i]))
                {
                    BaseIds[i] = IsSingleton
                        ? singletonCount++
                        : filterCount++;
                    registeredTypeIds.Add(BaseTypes[i], BaseIds[i]);
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