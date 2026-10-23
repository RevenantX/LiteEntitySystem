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

    internal struct BaseTypeInfo
    {
        public readonly Type Type;
        public readonly bool IsSingleton;
        public ushort Id;

        public BaseTypeInfo(Type type)
        {
            Type = type;
            IsSingleton = type.IsSubclassOf(EntityClassData.SingletonEntityType);
            Id = ushort.MaxValue;
        }
    }
    
    internal struct EntityClassData
    {
        public readonly string ClassEnumName;
        
        public readonly ushort ClassId;
        public readonly ushort FilterId;
        public readonly bool IsSingleton;
        public readonly int FieldsCount;
        public readonly int FieldsFlagsSize;
        public readonly int FixedFieldsSize;
        public readonly int PredictedSize;
        public readonly EntityFieldInfo[] Fields;
        public readonly SyncableFieldInfo[] SyncableFields;
        public readonly SyncableFieldInfo[] SyncableFieldsCustomRollback;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;
        public readonly int LagCompensatedCount;
        public readonly EntityFlags Flags;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        public readonly BaseTypeInfo[] BaseTypes;
        public RpcFieldInfo[] RemoteCallsClient;
        public readonly Type Type;

        /// <summary>
        /// Hash of everything that must match between peers: wire layout of fields (names, types with their
        /// struct contents, sizes, flags, fixed offsets), syncable field types and remote calls
        /// </summary>
        public readonly ulong LayoutHash;

        private readonly int[] _ownedRollbackFields;
        private readonly int[] _remoteRollbackFields;
        
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        internal static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableFieldType = typeof(SyncableField);
        private static readonly Type EntityLogicType = typeof(EntityLogic);
        
        private readonly Queue<byte[]> _dataCache;
        private readonly int _dataCacheSize;
        private readonly int _maxHistoryCount;
        private readonly int _historyStart;
        
        public Span<byte> GetLastServerData(InternalEntity e) => new (e.IOBuffer, 0, PredictedSize);

        public int[] GetRollbackFields(bool isOwned) =>
            isOwned ? _ownedRollbackFields : _remoteRollbackFields;
        
        //here Offset used because SyncableFields doesn't support LagCompensated fields
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
        
        public byte[] AllocateDataCache()
        {
            if (_dataCache.Count > 0)
            {
                byte[] data = _dataCache.Dequeue();
                Array.Clear(data, 0, data.Length);
                return data;
            }
            return new byte[_dataCacheSize];
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

        public EntityClassData(EntityManager entityManager, ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            _dataCache = new Queue<byte[]>();
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            InterpolatedCount = 0;
            RemoteCallsClient = null;
            ClassId = typeInfo.ClassId;
            ClassEnumName = typeInfo.ClassName;
            Flags = 0;
            EntityConstructor = typeInfo.Constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;

            var tempBaseTypes = Utils.GetBaseTypes(entType, InternalEntityType, false, true);
            BaseTypes = new BaseTypeInfo[tempBaseTypes.Count];
            for (int i = 0; i < BaseTypes.Length; i++)
                BaseTypes[i] = new BaseTypeInfo(tempBaseTypes.Pop());
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<SyncableFieldInfo>();
            var syncableFieldsWithCustomRollback = new List<SyncableFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();
            var ownedRollbackFields = new List<int>();
            var remoteRollbackFields = new List<int>();
            
            var layoutHash = new LayoutHasher();
            
            var allTypesStack = Utils.GetBaseTypes(entType, InternalEntityType, true, true);
            while(allTypesStack.Count > 0)
            {
                var baseType = allTypesStack.Pop();
                var processedFields = Utils.GetProcessedFields(baseType);
                //add subclasses names only if they had sync members
                bool hasLayoutMembers = false;
                foreach (var field in processedFields)
                {
                    var fieldType = field.FieldType;
                    if (field.IsStatic)
                    {
                        if (Utils.IsRemoteCallType(fieldType))
                        {
                            hasLayoutMembers = true;
                            break;
                        }
                    }
                    else if ((fieldType.IsGenericType && !fieldType.IsArray && fieldType.GetGenericTypeDefinition() == typeof(SyncVar<>)) ||
                             fieldType.IsSubclassOf(SyncableFieldType))
                    {
                        hasLayoutMembers = true;
                        break;
                    }
                }
                if (hasLayoutMembers)
                    layoutHash.Add(baseType.Name);
                
                var setFlagsAttribute = baseType.GetCustomAttribute<EntityFlagsAttribute>();
                Flags |= setFlagsAttribute != null ? setFlagsAttribute.Flags : 0;
                
                //cache fields
                foreach (var field in processedFields)
                {
                    var ft = field.FieldType;
                    if(Utils.IsRemoteCallType(ft) && !field.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {field.Name})");
                    
                    if(field.IsStatic)
                    {
                        if (Utils.IsRemoteCallType(ft))
                            layoutHash.AddField(field);
                        continue;
                    }
                    
                    var syncVarFlags = field.GetCustomAttribute<SyncVarFlags>() ?? baseType.GetCustomAttribute<SyncVarFlags>();
                    var syncFlags = syncVarFlags?.Flags ?? SyncFlags.None;
                    int offset = Utils.GetFieldOffset(field);
                    
                    //syncvars
                    if (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        layoutHash.AddField(field);
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
                            InterpolatedCount++;
                        }
                        var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}", valueTypeProcessor, offset, syncVarFlags?.Flags ?? SyncFlags.None);
                        if (syncFlags.HasFlagFast(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(fieldInfo);
                            LagCompensatedSize += fieldSize;
                        }

                        if (fieldInfo.IsPredicted)
                            PredictedSize += fieldSize;

                        fields.Add(fieldInfo);
                        FixedFieldsSize += fieldSize;
                    }
                    else if (ft.IsSubclassOf(SyncableFieldType))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception($"Syncable fields should be readonly! (Class: {entType} Field: {field.Name})");
                        
                        //generic arguments (SyncArray<TStruct> etc) aren't visible in Fields
                        layoutHash.AddField(field);
                        layoutHash.Add((int)syncFlags);
                        syncableFields.Add(new SyncableFieldInfo(offset, syncFlags));
                        
                        //add custom rollbacked separately
                        if (ft.IsSubclassOf(typeof(SyncableFieldCustomRollback)))
                            syncableFieldsWithCustomRollback.Add(new SyncableFieldInfo(offset, syncFlags));
                        
                        var syncableFieldTypesWithBase = Utils.GetBaseTypes(ft, SyncableFieldType, true, true);
                        while(syncableFieldTypesWithBase.Count > 0)
                        {
                            var syncableType = syncableFieldTypesWithBase.Pop();
                            //syncable fields
                            foreach (var syncableField in Utils.GetProcessedFields(syncableType))
                            {
                                var syncableFieldType = syncableField.FieldType;
                                if(Utils.IsRemoteCallType(syncableFieldType) && !syncableField.IsStatic)
                                    throw new Exception($"RemoteCalls should be static! (Class: {syncableType} Field: {syncableField.Name})");
                                
                                if (syncableField.IsStatic && Utils.IsRemoteCallType(syncableFieldType))
                                    layoutHash.AddField(syncableField);
                                
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
                                
                                var mergedSyncFlags = (syncableField.GetCustomAttribute<SyncVarFlags>()?.Flags ?? SyncFlags.None) | (syncVarFlags?.Flags ?? SyncFlags.None);
                                if (mergedSyncFlags.HasFlagFast(SyncFlags.OnlyForOwner) &&
                                    mergedSyncFlags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                                {
                                    Logger.LogWarning($"{SyncFlags.OnlyForOwner} and {SyncFlags.OnlyForOtherPlayers} flags can't be used together! Field: {syncableType} - {syncableField.Name}");
                                }
                                if (mergedSyncFlags.HasFlagFast(SyncFlags.AlwaysRollback) &&
                                    mergedSyncFlags.HasFlagFast(SyncFlags.NeverRollBack))
                                {
                                    Logger.LogWarning($"{SyncFlags.AlwaysRollback} and {SyncFlags.NeverRollBack} flags can't be used together! Field: {syncableType} - {syncableField.Name}");
                                }
                                
                                int syncvarOffset = Utils.GetFieldOffset(syncableField);
                                var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}:{syncableField.Name}", valueTypeProcessor, offset, syncvarOffset, mergedSyncFlags);
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
            // List.Sort is UNSTABLE, so a comparator that only ranks the interpolated flag leaves the order of
            // equal-ranked fields implementation-defined — which can differ across .NET runtimes and break the
            // cross-peer field layout. Tie-break by the (unique) field Name with Ordinal comparison to make the
            // order a total, environment-independent ordering.
            fields.Sort((a, b) =>
            {
                int wa = a.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                int wb = b.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                if (wa != wb) return wb - wa;
                return string.CompareOrdinal(a.Name, b.Name);
            });
            Fields = fields.ToArray();
            SyncableFields = syncableFields.ToArray();
            SyncableFieldsCustomRollback = syncableFieldsWithCustomRollback.ToArray();
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
                    
                    //singletons can't be owned
                    if (!IsSingleton)
                        ownedRollbackFields.Add(i);
                    
                    if(field.Flags.HasFlagFast(SyncFlags.AlwaysRollback))
                        remoteRollbackFields.Add(i);
                }
                else
                {
                    field.PredictedOffset = -1;
                }
            }
            
            layoutHash.Add(ClassId);
            layoutHash.Add(ClassEnumName);
            layoutHash.Add((int)Flags);
            layoutHash.Add(IsSingleton ? 1 : 0);
            layoutHash.Add(FixedFieldsSize);
            layoutHash.Add(PredictedSize);
            layoutHash.Add(LagCompensatedSize);
            foreach (var field in Fields)
            {
                //Offset isn't hashed: it's managed memory offset and differs between runtimes
                layoutHash.Add(field.Name);
                layoutHash.Add(field.IntSize);
                layoutHash.Add((int)field.FieldType);
                layoutHash.Add((int)field.Flags);
                layoutHash.Add(field.FixedOffset);
                layoutHash.Add(field.PredictedOffset);
            }
            LayoutHash = layoutHash.Value;
            
            //cache rollbackFields
            _ownedRollbackFields = ownedRollbackFields.ToArray();
            _remoteRollbackFields = remoteRollbackFields.ToArray();
            
            _maxHistoryCount = (byte)entityManager.MaxHistorySize;
            int historySize = entType.IsSubclassOf(EntityLogicType) ? (_maxHistoryCount + 1) * LagCompensatedSize : 0;
            if (entityManager.IsServer)
            {
                _dataCacheSize = historySize + StateSerializer.HeaderSize + FixedFieldsSize;
                _historyStart = StateSerializer.HeaderSize + FixedFieldsSize;
            }
            else
            {
                _dataCacheSize = PredictedSize + historySize;
                _historyStart = PredictedSize;
            }

            Type = entType;
        }

        public void PrepareBaseTypes(Dictionary<Type, ushort> registeredTypeIds, ref ushort singletonCount, ref ushort filterCount)
        {
            if (Type == null)
                return;
            for (int i = 0; i < BaseTypes.Length; i++)
            {
                ref var baseTypeInfo = ref BaseTypes[i];
                if (!registeredTypeIds.TryGetValue(baseTypeInfo.Type, out baseTypeInfo.Id))
                {
                    baseTypeInfo.Id = baseTypeInfo.IsSingleton
                        ? singletonCount++
                        : filterCount++;
                    registeredTypeIds.Add(baseTypeInfo.Type, baseTypeInfo.Id);
                    //Logger.Log($"Register Base {i} type of {ClassId} - type: {_baseTypes[i]}, id: {BaseIds[i]}");
                }
            }
        }
    }

    /// <summary>
    /// FNV1a 64 bit hash of class layout. Types are hashed by name and, for structs, by their fields
    /// in declaration (= memory) order, so a reordered or changed struct (also partial one) changes the hash.
    /// </summary>
    internal sealed class LayoutHasher
    {
        private readonly HashSet<Type> _walkedTypes = new();

        public ulong Value { get; private set; } = 14695981039346656037UL; //FNV1a offset

        public void Add(string str)
        {
            Add(str.Length);
            foreach (char c in str)
                AddByte(c);
        }

        public void Add(int value)
        {
            for (int i = 0; i < 4; i++)
                AddByte(value >> (i * 8));
        }

        public void Add(ulong value)
        {
            for (int i = 0; i < 8; i++)
                AddByte((int)(value >> (i * 8)));
        }

        public void AddField(FieldInfo field)
        {
            Add(field.Name);
            AddType(field.FieldType);
        }

        public void AddType(Type t)
        {
            Add(TypeName(t));
            Walk(t);
        }

        private void AddByte(int b)
        {
            Value ^= (byte)b;
            Value *= 1099511628211UL; //prime
        }

        //contents of struct and generic arguments, name only for classes
        private void Walk(Type t)
        {
            if (!_walkedTypes.Add(t))
                return;
            if (t.IsArray || t.IsPointer)
            {
                Walk(t.GetElementType());
                return;
            }
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                    Walk(arg);
            }
            if (t.IsEnum)
            {
                Add(TypeName(t.GetEnumUnderlyingType()));
                return;
            }
            if (!t.IsValueType || t.IsPrimitive)
                return;
            var layout = t.StructLayoutAttribute;
            if (layout != null)
            {
                Add((int)layout.Value);
                Add(layout.Pack);
                Add(layout.Size);
            }
            //metadata order, not sorted: it's the memory layout of the struct
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Add(field.Name);
                Add(TypeName(field.FieldType));
                Walk(field.FieldType);
            }
        }

        //assembly independent name (FullName of generic types contains assembly qualified arguments)
        private static string TypeName(Type t)
        {
            if (t.IsArray)
                return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsPointer)
                return TypeName(t.GetElementType()) + "*";
            if (!t.IsGenericType)
                return t.FullName ?? t.Name;
            var args = t.GetGenericArguments();
            var names = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
                names[i] = TypeName(args[i]);
            return t.GetGenericTypeDefinition().FullName + "[" + string.Join(",", names) + "]";
        }
    }
}
