using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    internal enum FixedFieldType
    {
        None,
        EntityId,
        String
    }
    public partial class EntityManager
    {
        internal readonly struct EntityFieldInfo
        {
            public readonly int Offset;
            public readonly uint Size;
            public readonly int IntSize;
            public readonly UIntPtr PtrSize;
            public readonly FixedFieldType Type;

            public EntityFieldInfo(int offset, int size, FixedFieldType type)
            {
                Offset = offset;
                Size = (uint)size;
                IntSize = size;
                PtrSize = (UIntPtr)Size;
                Type = type;
            }
        }
        
        internal sealed class EntityClassData
        {
            public readonly ushort ClassId;
            public readonly int FilterId;

            public readonly bool IsSingleton;
            public readonly int[] BaseIds;

            public readonly int FieldsCount;
            public readonly int FixedFieldsSize;
            public readonly EntityFieldInfo[] Fields;
            
            public readonly InterpolatorDelegate[] InterpolatedMethods;
            public int InterpolatedFieldsSize;

            public readonly bool IsUpdateable;
            public readonly bool IsServerOnly;

            public readonly Type[] BaseTypes;

            public readonly Func<EntityParams, InternalEntity> EntityConstructor;

            public EntityClassData(
                EntityManager manager, 
                Type entType, 
                ushort classId,
                Func<EntityParams, InternalEntity> constructor)
            {
                ClassId = classId;
                IsUpdateable = entType.GetCustomAttribute<UpdateableEntity>() != null;
                IsServerOnly = entType.GetCustomAttribute<ServerOnly>() != null;
                EntityConstructor = constructor;
                IsSingleton = entType.IsSubclassOf(typeof(SingletonEntityLogic));
                FilterId = IsSingleton ? manager._singletonRegisteredCount++ : manager._filterRegisteredCount++;

                var baseTypes = new List<Type>();
                var baseType = entType.BaseType;
                while (baseType != typeof(InternalEntity))
                {
                    baseTypes.Add(baseType);
                    baseType = baseType!.BaseType;
                }

                BaseTypes = baseTypes.ToArray();
                BaseIds = new int[baseTypes.Count];
                
                var interpolatedMethods = new List<InterpolatorDelegate>();
                var fields = new List<EntityFieldInfo>();

                //add here to baseTypes to add fields
                baseTypes.Add(entType);
                baseTypes.Add(typeof(InternalEntity));
                
                foreach (var typesToCheck in baseTypes)
                {
                    foreach (var field in typesToCheck.GetFields(
                                 BindingFlags.Instance | 
                                 BindingFlags.Public | 
                                 BindingFlags.NonPublic | 
                                 BindingFlags.DeclaredOnly))
                    {
                        var syncVarAttribute = field.GetCustomAttribute<SyncVar>();
                        var ft = field.FieldType;
                        if(syncVarAttribute == null)
                            continue;
                        
                        int offset = Marshal.ReadInt32(field.FieldHandle.Value + 3 * IntPtr.Size) & 0xFFFFFF;
                        
                        if (ft.IsValueType)
                        {
                            int fieldSize = Marshal.SizeOf(ft);

                            if (syncVarAttribute.IsInterpolated)
                            {
                                if (!InterpolatedData.TryGetValue(ft, out var interpolatedInfo))
                                    throw new Exception($"No info how to interpolate: {ft}");
                                interpolatedMethods.Insert(0, interpolatedInfo);
                                fields.Insert(0, new EntityFieldInfo(offset, fieldSize, FixedFieldType.None));
                                InterpolatedFieldsSize += fieldSize;
                            }
                            else
                            {
                                fields.Add(new EntityFieldInfo(offset, ft == typeof(bool) ? 1 : fieldSize, FixedFieldType.None));
                            }


                            FixedFieldsSize += fieldSize;
                        }
                        else if (ft == typeof(EntityLogic) || ft.IsSubclassOf(typeof(InternalEntity)))
                        {
                            fields.Add(new EntityFieldInfo(offset, 2, FixedFieldType.EntityId));
                            FixedFieldsSize += 2;
                        }
                        else if (ft == typeof(string))
                        {
                            fields.Add(new EntityFieldInfo(offset, 0, FixedFieldType.String));
                        }
                        else
                        {
                            Logger.LogError($"UnsupportedSyncVar: {field.Name}");
                        }
                    }
                }
                
                InterpolatedMethods = interpolatedMethods.ToArray();
                Fields = fields.ToArray();
                FieldsCount = Fields.Length;
            }
        }
        
        protected static class EntityClassInfo<T>
        {
            // ReSharper disable once StaticMemberInGenericType
            internal static ushort ClassId;
        }

        private SingletonEntityLogic[] _singletonEntities;
        private EntityFilter[] _entityFilters;
        private ushort _filterRegisteredCount;
        private ushort _singletonRegisteredCount;
        private readonly Dictionary<Type, int> _registeredTypeIds = new Dictionary<Type, int>();
        private int _entityEnumSize = -1;

        internal readonly EntityClassData[] ClassDataDict = new EntityClassData[ushort.MaxValue];

        public void RegisterEntity<TEntity, TEnum>(TEnum id, Func<EntityParams, TEntity> constructor)
            where TEntity : InternalEntity where TEnum : Enum
        {
            if (_entityEnumSize == -1)
                _entityEnumSize = Enum.GetValues(typeof(TEnum)).Length;
            
            var entType = typeof(TEntity);

            ushort classId = (ushort)(object)id;
            ref var classData = ref ClassDataDict[classId];
            classData = new EntityClassData(this, entType, classId, constructor);
            EntityClassInfo<TEntity>.ClassId = classId;
            _registeredTypeIds.Add(entType, classData.FilterId);
            
            Logger.Log($"Register entity. Id: {id.ToString()} ({entType}), baseTypes: {classData.BaseTypes.Length}, FilterId: {classData.FilterId}");
        }

        private void SetupEntityInfo()
        {
            for (int e = 0; e < _entityEnumSize; e++)
            {
                //map base ids
                var classData = ClassDataDict[e];
                if(classData == null)
                    continue;

                var baseTypes = classData.BaseTypes;
                var baseIds = classData.BaseIds;
                
                for (int i = 0; i < baseIds.Length; i++)
                {
                    if (!_registeredTypeIds.TryGetValue(baseTypes[i], out baseIds[i]))
                    {
                        baseIds[i] = classData.IsSingleton
                            ? _singletonRegisteredCount++
                            : _filterRegisteredCount++;
                        _registeredTypeIds.Add(baseTypes[i], baseIds[i]);
                    }
                    Logger.Log($"Base type of {classData.ClassId} - {baseTypes[i]}");
                }
            }

            _entityFilters = new EntityFilter[_filterRegisteredCount];
            _singletonEntities = new SingletonEntityLogic[_singletonRegisteredCount];
        }
    }
}