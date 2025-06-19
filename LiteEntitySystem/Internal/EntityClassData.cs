using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace LiteEntitySystem.Internal
{
    /// <summary>
    /// Holds metadata for a SyncableField, including its relationship to a parent container
    /// and the relative offset of the field within that container.
    /// </summary>
    internal struct SyncableFieldInfo
    {
        /// <summary>
        /// For debug: the fully qualified name (e.g. "Entity-MovementComponent").
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// Absolute byte offset of the containing object (entity or parent SyncableField) within the root InternalEntity.
        /// </summary>
        // public readonly int ParentOffset;

        /// <summary>
        /// Byte offset map to this SyncableField within its parent InternalEntity.
        /// </summary>
        public readonly int[] Offsets;

        /// <summary>
        /// SyncFlags controlling rollback, prediction, interpolation, etc.
        /// </summary>
        public readonly SyncFlags Flags;

        /// <summary>
        /// Offset into the RPC table (initialized later).
        /// </summary>
        public ushort RPCOffset;

        /// <summary>
        /// Constructs a new SyncableFieldInfo.
        /// </summary>
        /// <param name="name">Debug name of the field.</param>
        /// <param name="parentOffset">Absolute offset of the parent container.</param>
        /// <param name="offset">Relative offset within the parent container.</param>
        /// <param name="flags">SyncFlags for this field.</param>
        public SyncableFieldInfo(string name, int[] offsets, SyncFlags flags)
        {
            Name = name;
            Offsets = offsets;
            Flags = flags;
            RPCOffset = ushort.MaxValue;
        }
    }


    internal readonly struct RpcFieldInfo
    {
        public readonly int[] SyncableOffsets;
        public readonly MethodCallDelegate Method;

        public RpcFieldInfo(MethodCallDelegate method)
    {
            SyncableOffsets = [-1];
            Method = method;
        }

        public RpcFieldInfo(int[] syncableOffsets, MethodCallDelegate method)
        {
            SyncableOffsets = syncableOffsets;
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
        public readonly int InterpolatedFieldsSize;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;
        public readonly int LagCompensatedCount;
        public readonly EntityFlags Flags;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        public readonly BaseTypeInfo[] BaseTypes;
        public RpcFieldInfo[] RemoteCallsClient;
        public readonly Type Type;

        private readonly EntityFieldInfo[] _ownedRollbackFields;
        private readonly EntityFieldInfo[] _remoteRollbackFields;

        private static readonly Type InternalEntityType = typeof(InternalEntity);
        internal static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableFieldType = typeof(SyncableField);
        private static readonly Type EntityLogicType = typeof(EntityLogic);

        private readonly Queue<byte[]> _dataCache;
        private readonly int _dataCacheSize;
        private readonly int _maxHistoryCount;
        private readonly int _historyStart;

        public Span<byte> ClientInterpolatedPrevData(InternalEntity e) => new(e.IOBuffer, 0, InterpolatedFieldsSize);
        public Span<byte> ClientInterpolatedNextData(InternalEntity e) => new(e.IOBuffer, InterpolatedFieldsSize, InterpolatedFieldsSize);
        public Span<byte> ClientPredictedData(InternalEntity e) => new(e.IOBuffer, InterpolatedFieldsSize * 2, PredictedSize);

        public EntityFieldInfo[] GetRollbackFields(bool isOwned) =>
            isOwned ? _ownedRollbackFields : _remoteRollbackFields;

        public unsafe void WriteHistory(EntityLogic e, ushort tick)
        {
            int historyOffset = ((tick % _maxHistoryCount) + 1) * LagCompensatedSize;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.WriteTo(e, field.Offsets, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }

        public unsafe void LoadHistroy(NetPlayer player, EntityLogic e)
        {
            int historyAOffset = ((player.StateATick % _maxHistoryCount) + 1) * LagCompensatedSize;
            int historyBOffset = ((player.StateBTick % _maxHistoryCount) + 1) * LagCompensatedSize;
            int historyCurrent = 0;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.LoadHistory(
                        e,
                        field.Offsets,
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
                    field.TypeProcessor.SetFrom(e, field.Offsets, history + historyOffset);
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

        // PendingField carries both the FieldInfo and its metadata
        private struct PendingField
        {
            public FieldInfo Field;
            public string NamePrefix;
            public List<int> Offsets;
            public SyncVarFlags Flags;
            public Type DeclaringType;
        }

        public EntityClassData(EntityManager entityManager, ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            _dataCache = new Queue<byte[]>();
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            InterpolatedCount = 0;
            InterpolatedFieldsSize = 0;
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
            var lagCompensatedFields = new List<EntityFieldInfo>();
            var ownedRollbackFields = new List<EntityFieldInfo>();
            var remoteRollbackFields = new List<EntityFieldInfo>();

            // Logger.LogError($"Class Data for : {entType}");

            var allTypesStack = Utils.GetBaseTypes(entType, InternalEntityType, true, true);
            while (allTypesStack.Count > 0)
            {
                var baseType = allTypesStack.Pop();

                var setFlagsAttribute = baseType.GetCustomAttribute<EntityFlagsAttribute>();
                Flags |= setFlagsAttribute != null ? setFlagsAttribute.Flags : 0;

                // Seed the PendingField stack with top-level fields
                var fieldsStack = new Stack<PendingField>();
                foreach (var f in Utils.GetProcessedFields(baseType))
                {
                    var flags = f.GetCustomAttribute<SyncVarFlags>()
                                ?? baseType.GetCustomAttribute<SyncVarFlags>()
                                ?? new SyncVarFlags(SyncFlags.None);

                    if (Utils.IsRemoteCallType(f.FieldType) && !f.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {f.Name})");
                    if (f.IsStatic)
                        continue;

                    var offset = Utils.GetFieldOffset(f);

                    fieldsStack.Push(new PendingField
                    {
                        Field = f,
                        NamePrefix = $"{baseType.Name}-{f.Name}",
                        Offsets = new List<int> { offset },
                        Flags = flags,
                        DeclaringType = baseType
                    });
                }

                // Process each pending field (handles nested SyncableFields inline)
                while (fieldsStack.Count > 0)
                {
                    var ctx = fieldsStack.Pop();
                    var field = ctx.Field;
                    var ft = field.FieldType;
                    var name = ctx.NamePrefix;
                    var offsetMap = ctx.Offsets; // chain of offsets to this field
                    var syncVarFlags = ctx.Flags;
                    var syncFlags = syncVarFlags.Flags;

                    if (Utils.IsRemoteCallType(ft) && !field.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {field.Name})");
                    if (field.IsStatic)
                        continue;

                    // --- SyncVar<T> handling ---
                    if (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        var innerType = ft.GetGenericArguments()[0];
                        if (innerType.IsEnum)
                            innerType = innerType.GetEnumUnderlyingType();

                        if (!ValueTypeProcessor.Registered.TryGetValue(innerType, out var processor))
                        {
                            Logger.LogError($"Unregistered field type: {innerType}");
                            continue;
                        }

                        var fieldSize = processor.Size;

                        // choose the correct constructor:
                        EntityFieldInfo ef;
                        if (ctx.DeclaringType.IsSubclassOf(SyncableFieldType))
                        {
                            // nested SyncableField's SyncVar<…>
                            ef = new EntityFieldInfo(name, processor, offsetMap.ToArray(), syncVarFlags, FieldType.SyncableSyncVar);
                            GD.Print($"Registered syncable sync var field: {name} with offset {string.Join(",", offsetMap)}");
                        }
                        else
                        {
                            // top-level field on the entity itself
                            ef = new EntityFieldInfo(name, processor, offsetMap.ToArray(), syncVarFlags, FieldType.SyncVar);
                            GD.Print($"Registered field: {name} with offsets {string.Join(",", offsetMap)}");
                        }

                        if (syncFlags.HasFlagFast(SyncFlags.Interpolated) && !ft.IsEnum)
                        {
                            InterpolatedFieldsSize += fieldSize;
                            InterpolatedCount++;
                        }

                        if (syncFlags.HasFlagFast(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(ef);
                            LagCompensatedSize += fieldSize;
                        }

                        if (ef.IsPredicted)
                            PredictedSize += fieldSize;

                        fields.Add(ef);
                        FixedFieldsSize += fieldSize;
                    }
                    // --- nested SyncableFieldType ---
                    else if (ft.IsSubclassOf(SyncableFieldType))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception($"Syncable fields should be readonly! (Class: {entType} Field: {field.Name})");

                        syncableFields.Add(new SyncableFieldInfo(name, offsetMap.ToArray(), syncVarFlags.Flags));
                        Logger.Log($"Registered syncable field {name} with offsets {string.Join(",", offsetMap)}");

                        var nestedTypes = Utils.GetBaseTypes(ft, SyncableFieldType, true, true);
                        foreach (var nest in nestedTypes)
                        {
                            foreach (var nestedField in Utils.GetProcessedFields(nest))
                            {
                                var nestedFlags = nestedField.GetCustomAttribute<SyncVarFlags>()
                                                 ?? nest.GetCustomAttribute<SyncVarFlags>()
                                                 ?? syncVarFlags;

                                var nestFieldOffset = Utils.GetFieldOffset(nestedField);

                                // build new chain: existing chain + nestFieldOffset
                                var nestedOffsetMap = new List<int>(offsetMap) { nestFieldOffset };

                                fieldsStack.Push(new PendingField
                                {
                                    Field = nestedField,
                                    NamePrefix = $"{name}:{nestedField.Name}",
                                    Offsets = nestedOffsetMap,
                                    Flags = nestedFlags,
                                    DeclaringType = nest
                                });
                            }
                        }
                        continue;
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
            FieldsFlagsSize = (FieldsCount - 1) / 8 + 1;
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
                    ownedRollbackFields.Add(field);
                    if (field.Flags.HasFlagFast(SyncFlags.AlwaysRollback))
                        remoteRollbackFields.Add(field);
                }
                else
                {
                    field.PredictedOffset = -1;
                }
            }

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
                _dataCacheSize = InterpolatedFieldsSize * 2 + PredictedSize + historySize;
                _historyStart = InterpolatedFieldsSize * 2 + PredictedSize;
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

    // ReSharper disable once UnusedTypeParameter
    internal static class EntityClassInfo<T>
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static ushort ClassId;
    }
}