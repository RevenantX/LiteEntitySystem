using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        private bool _isFinished;
        private ulong _resultHash = 14695981039346656037UL; //FNV1a offset

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
            RegisteredTypes.Add(entityType, new RegisteredTypeInfo(classId, className, constructor));
            MaxId = Math.Max(MaxId, classId);
        }

        /// <summary>
        /// Can be used to detect that server/client has difference
        /// </summary>
        /// <returns>hash</returns>
        public ulong EvaluateEntityClassDataHash()
        {
            //FNV1a 64 bit hash
            if (!_isFinished)
            {
                //don't hash localonly types
                foreach (var (entType, _) in RegisteredTypes
                    .Where(kv => !kv.Key.IsSubclassOf(typeof(AiControllerLogic)))
                    .OrderBy(kv => kv.Value.ClassId))
                {
                    var allTypesStack = Utils.GetBaseTypes(entType, typeof(InternalEntity), true, true);
                    while(allTypesStack.Count > 0)
                    {
                        foreach (var field in Utils.GetProcessedFields(allTypesStack.Pop()))
                        {
                            if (field.FieldType.IsSubclassOf(typeof(SyncableField)))
                            {
                                foreach (var syncableField in Utils.GetProcessedFields(field.FieldType))
                                    TryHashField(syncableField);
                            }
                            else
                            {
                                TryHashField(field);
                            }
                        }
                    }
                }
                _isFinished = true;
            }
            return _resultHash;

            void TryHashField(FieldInfo fi)
            {
                var ft = fi.FieldType;
                if ((fi.IsStatic && Utils.IsRemoteCallType(ft)) || 
                    (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>)))
                {
                    string ftName = ft.Name + (ft.IsGenericType ? ft.GetGenericArguments()[0].Name : string.Empty);
                    for (int i = 0; i < ftName.Length; i++)
                    {
                        _resultHash ^= ftName[i];
                        _resultHash *= 1099511628211UL; //prime
                    }
                }
            }
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
