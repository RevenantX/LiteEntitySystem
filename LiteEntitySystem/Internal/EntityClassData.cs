using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    internal readonly struct EntityClassData
    {
        public readonly ushort ClassId;
        public readonly ushort FilterId;
        public readonly bool IsSingleton;
        public readonly ushort[] BaseIds;
        public readonly bool IsLocalOnly;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        
        private readonly bool _isCreated;
        private readonly Type[] _baseTypes;
        
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        private static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);

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

        public EntityClassData(ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            ClassId = typeInfo.ClassId;
            IsLocalOnly = entType.GetCustomAttributes(typeof(LocalOnly), true).Length > 0;
            EntityConstructor = typeInfo.Constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;

            var baseTypes = GetBaseTypes(entType, InternalEntityType, false);
            _baseTypes = baseTypes.ToArray();
            BaseIds = new ushort[baseTypes.Count];

            //add here to baseTypes to add fields
            baseTypes.Insert(0, typeof(InternalEntity));
            baseTypes.Add(entType);

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