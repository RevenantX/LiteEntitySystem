using System;
using System.Collections.Generic;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    internal readonly struct RegisteredTypeInfo
    {
        public readonly ushort ClassId;
        public readonly EntityConstructor<InternalEntity> Constructor;

        public RegisteredTypeInfo(ushort classId, EntityConstructor<InternalEntity> constructor)
        {
            ClassId = classId;
            Constructor = constructor;
        }
    }
    
    public abstract class EntityTypesMap
    {
        internal ushort MaxId;
        internal readonly Dictionary<Type, RegisteredTypeInfo> RegisteredTypes = new Dictionary<Type, RegisteredTypeInfo>();
    }

    /// <summary>
    /// Entity types map that will be used for EntityManager
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class EntityTypesMap<T> : EntityTypesMap where T : unmanaged, Enum
    {
        /// <summary>
        /// Register new entity type that will be used in game
        /// </summary>
        /// <param name="id">Enum value that will describe entity class id</param>
        /// <param name="constructor">Constructor of entity</param>
        /// <typeparam name="TEntity">Type of entity</typeparam>
        public EntityTypesMap<T> Register<TEntity>(T id, EntityConstructor<TEntity> constructor) where TEntity : InternalEntity 
        {
            ushort classId = (ushort)(id.GetEnumValue()+1);
            EntityClassInfo<TEntity>.ClassId = classId;
            RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(classId, constructor));
            MaxId = Math.Max(MaxId, classId);
            return this;
        }
    }
}