using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    public unsafe delegate void MethodCallDelegate(void* ent, void* previousValue);

    public static class MethodCallGenerator
    {
        //public for AOT
        public static unsafe MethodCallDelegate Generate<TEnt, TValue>(MethodInfo method)
        {
            var typedDelegate = (Action<TEnt, TValue>)method.CreateDelegate(typeof(Action<TEnt, TValue>));
            return (ent, previousValue) =>
            {
                typedDelegate(
                    Unsafe.AsRef<TEnt>(ent),
                    previousValue == null ? default : Unsafe.AsRef<TValue>(previousValue));
            };
        }
        
        internal static MethodCallDelegate GetOnSyncDelegate(Type entityType, Type valueType, string methodName)
        {
            if (string.IsNullOrEmpty(methodName))
                return null;
            
            var method = entityType.GetMethod(
                methodName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly |
                BindingFlags.NonPublic);
            if (method == null)
            {
                Logger.LogError($"Method: {methodName} not found in {entityType}");
                return null;
            }

            return (MethodCallDelegate)typeof(MethodCallGenerator)
                .GetMethod(nameof(Generate))
                !.MakeGenericMethod(entityType, valueType)
                .Invoke(null, new object[] { method });
        }
    }

    internal struct EntityFieldInfo
    {
        public readonly int FieldOffset;
        public readonly uint Size;
        public readonly int IntSize;
        public readonly UIntPtr PtrSize;
        public readonly bool IsEntity;
        public readonly MethodCallDelegate OnSync;
        public readonly InterpolatorDelegate Interpolator;

        public int FixedOffset;

        public EntityFieldInfo(
            MethodCallDelegate onSync, 
            InterpolatorDelegate interpolator,
            int offset,
            int size,
            bool isEntity)
        {
            FieldOffset = offset;
            Size = (uint)size;
            IntSize = size;
            PtrSize = (UIntPtr)Size;
            IsEntity = isEntity;
            OnSync = onSync;
            Interpolator = interpolator;
            FixedOffset = 0;
        }
    }
    
    internal sealed class EntityClassData
    {
        public readonly ushort ClassId;
        public readonly int FilterId;
        public readonly bool IsSingleton;
        public readonly int[] BaseIds;
        public readonly int FieldsCount;
        public readonly int FieldsFlagsSize;
        public readonly int FixedFieldsSize;
        public readonly EntityFieldInfo[] Fields;
        public readonly EntityFieldInfo[] SyncableFields;
        public readonly int InterpolatedFieldsSize;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;

        public readonly bool IsUpdateable;
        public readonly bool IsServerOnly;
        public readonly Type[] BaseTypes;
        public readonly Func<EntityParams, EntityManager.InternalEntity> EntityConstructor;
        public readonly Dictionary<MethodInfo, RemoteCall> RemoteCalls = new Dictionary<MethodInfo, RemoteCall>();
        public readonly MethodCallDelegate[] RemoteCallsClient = new MethodCallDelegate[255];
        public readonly MethodCallDelegate[] SyncableRemoteCallsClient = new MethodCallDelegate[255];
        public readonly Dictionary<MethodInfo, SyncableRemoteCall> SyncableRemoteCalls =
            new Dictionary<MethodInfo, SyncableRemoteCall>();

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
        
        public EntityClassData(
            int filterId, 
            Type entType, 
            ushort classId,
            Func<EntityParams, EntityManager.InternalEntity> constructor)
        {
            ClassId = classId;
            IsUpdateable = entType.GetCustomAttribute<UpdateableEntity>() != null;
            IsServerOnly = entType.GetCustomAttribute<ServerOnly>() != null;
            EntityConstructor = constructor;
            IsSingleton = entType.IsSubclassOf(typeof(SingletonEntityLogic));
            FilterId = filterId;

            var baseTypes = GetBaseTypes(entType, typeof(EntityManager.InternalEntity), false);
            BaseTypes = baseTypes.ToArray();
            BaseIds = new int[baseTypes.Count];
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<EntityFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();

            //add here to baseTypes to add fields
            baseTypes.Insert(0, typeof(EntityManager.InternalEntity));
            baseTypes.Add(entType);

            const BindingFlags bindingFlags = BindingFlags.Instance |
                                              BindingFlags.Public |
                                              BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly;

            byte rpcIndex = 0;
            byte syncableRpcIndex = 0;
            foreach (var baseType in baseTypes)
            {
                foreach (var method in baseType.GetMethods(bindingFlags))
                {
                    var remoteCallAttribute = method.GetCustomAttribute<RemoteCall>();
                    if(remoteCallAttribute == null)
                        continue;
                    
                    var parametrType = method.GetParameters()[0].ParameterType;
                    if (remoteCallAttribute.Id == byte.MaxValue)
                    {
                        remoteCallAttribute.Id = rpcIndex++;
                        remoteCallAttribute.DataSize = Marshal.SizeOf(parametrType);
                        if (rpcIndex == byte.MaxValue)
                            throw new Exception("254 is max RemoteCall methods");
                    }
                    RemoteCalls.Add(method, remoteCallAttribute);
                    RemoteCallsClient[remoteCallAttribute.Id] =
                        MethodCallGenerator.GetOnSyncDelegate(baseType, parametrType, method.Name);
                }
                foreach (var field in baseType.GetFields(bindingFlags))
                {
                    var syncVarAttribute = field.GetCustomAttribute<SyncVar>();
                    if(syncVarAttribute == null)
                        continue;
                    
                    var ft = field.FieldType;
                    int offset = Marshal.ReadInt32(field.FieldHandle.Value + 3 * IntPtr.Size) & 0xFFFFFF;
                    var onSyncMethod = MethodCallGenerator.GetOnSyncDelegate(baseType, ft, syncVarAttribute.MethodName);
                    
                    if (ft.IsValueType)
                    {
                        int fieldSize = ft == typeof(bool) ? 1 : Marshal.SizeOf(ft);
                        bool hasInterpolator = Interpolation.Methods.TryGetValue(ft, out var interpolator);
                        
                        if (syncVarAttribute.Flags.IsByteFlagSet(SyncFlags.Interpolated))
                        {
                            if (!hasInterpolator)
                                throw new Exception($"No info how to interpolate: {ft}");
                            InterpolatedFieldsSize += fieldSize;
                            InterpolatedCount++;
                        }

                        var fieldInfo = new EntityFieldInfo(onSyncMethod, interpolator, offset, fieldSize, false);
                        if (syncVarAttribute.Flags.IsByteFlagSet(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(fieldInfo);
                            LagCompensatedSize += fieldSize;
                        }
                        fields.Add(fieldInfo);
                        FixedFieldsSize += fieldSize;
                    }
                    else if (ft == typeof(EntityLogic) || ft.IsSubclassOf(typeof(EntityManager.InternalEntity)))
                    {
                        fields.Add(new EntityFieldInfo(onSyncMethod, null, offset, sizeof(ushort), true));
                        FixedFieldsSize += 2;
                    }
                    else if (ft.IsSubclassOf(typeof(SyncableField)))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception("Syncable fields should be readonly!");

                        //syncable rpcs
                        syncableFields.Add(new EntityFieldInfo(onSyncMethod, null, offset, 0, false));
                        foreach (var syncableType in GetBaseTypes(ft, typeof(SyncableField), true))
                        {
                            foreach (var method in syncableType.GetMethods(bindingFlags))
                            {
                                var rcAttribute = method.GetCustomAttribute<SyncableRemoteCall>();
                                if(rcAttribute == null)
                                    continue;
                                var parameterType = method.GetParameters()[0].ParameterType;
                                if (rcAttribute.Id == byte.MaxValue)
                                {
                                    rcAttribute.Id = syncableRpcIndex++;
                                    rcAttribute.DataSize = Marshal.SizeOf(parameterType.HasElementType ? parameterType.GetElementType() : parameterType);
                                }
                                if (syncableRpcIndex == byte.MaxValue)
                                    throw new Exception("254 is max RemoteCall methods");
                                SyncableRemoteCalls[method] = rcAttribute;
                                SyncableRemoteCallsClient[rcAttribute.Id] =
                                    MethodCallGenerator.GetOnSyncDelegate(syncableType, parameterType, method.Name);
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
            int fixedOffset = 0;
            for (int i = 0; i < Fields.Length; i++)
            {
                Fields[i].FixedOffset = fixedOffset;
                fixedOffset += Fields[i].IntSize;
            }
            
            SyncableFields = syncableFields.ToArray();
            FieldsCount = Fields.Length;
            FieldsFlagsSize = (FieldsCount-1) / 8 + 1;
            LagCompensatedFields = lagCompensatedFields.ToArray();
        }
    }
    
    internal static class EntityClassInfo<T>
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static ushort ClassId;
    }
}