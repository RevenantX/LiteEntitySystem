using System;
using System.Collections.Generic;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Information about registered type
    /// </summary>
    public readonly struct RegisteredTypeInfo
    {
        public readonly ushort ClassId;
        public readonly string ClassName;
        public readonly EntityConstructor<InternalEntity> Constructor;

        public RegisteredTypeInfo(ushort classId, string className, EntityConstructor<InternalEntity> constructor)
        {
            ClassId = classId;
            ClassName = className;
            Constructor = constructor;
        }
    }
    
    public abstract class EntityTypesMap
    {
        /// <summary>
        /// Maximum registered id. Ids start with 1
        /// Modify only in case when you know what you doing
        /// </summary>
        public ushort MaxId;

        /// <summary>
        /// Currently registered types
        /// Modify only in case when you know what you doing
        /// </summary>
        public readonly Dictionary<Type, RegisteredTypeInfo> RegisteredTypes = new();

        /// <summary>
        /// Register entity type with explicit class id (for maps that are not driven by enum)
        /// </summary>
        /// <param name="classId">Class id, must be greater than 0 and unique within map</param>
        /// <param name="className">Class name used in logs and debug</param>
        /// <param name="constructor">Constructor of entity</param>
        /// <typeparam name="TEntity">Type of entity</typeparam>
        protected void RegisterType<TEntity>(ushort classId, string className, EntityConstructor<TEntity> constructor) where TEntity : InternalEntity =>
            RegisterType(typeof(TEntity), classId, className, constructor);

        /// <summary>
        /// Register entity type known only at runtime with explicit class id
        /// </summary>
        /// <param name="entityType">Type of entity, must derive from InternalEntity</param>
        /// <param name="classId">Class id, must be greater than 0 and unique within map</param>
        /// <param name="className">Class name used in logs and debug</param>
        /// <param name="constructor">Constructor of entity, must create instances of entityType</param>
        protected void RegisterType(Type entityType, ushort classId, string className, EntityConstructor<InternalEntity> constructor)
        {
            if (classId == 0)
                throw new ArgumentException("Class id 0 is reserved", nameof(classId));
            if (!typeof(InternalEntity).IsAssignableFrom(entityType))
                throw new ArgumentException($"{entityType} is not an entity type", nameof(entityType));
            foreach (var typeInfo in RegisteredTypes.Values)
            {
                if (typeInfo.ClassId == classId)
                    throw new ArgumentException($"Class id {classId} is already registered", nameof(classId));
            }
            RegisteredTypes.Add(entityType, new RegisteredTypeInfo(classId, className, constructor));
            MaxId = Math.Max(MaxId, classId);
        }
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
            RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(classId, id.ToString(), constructor));
            MaxId = Math.Max(MaxId, classId);
            return this;
        }
    }
}
