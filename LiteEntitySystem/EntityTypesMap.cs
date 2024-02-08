using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        internal readonly Dictionary<Type, RegisteredTypeInfo> RegisteredTypes = new();
        private bool _isFinished;
        private const BindingFlags FieldsFlags = BindingFlags.Instance |
                                                 BindingFlags.Public |
                                                 BindingFlags.NonPublic |
                                                 BindingFlags.Static;
        
        /// <summary>
        /// Can be used to detect that server/client has difference
        /// </summary>
        /// <returns>hash</returns>
        public ulong EvaluateEntityClassDataHash()
        {
            //FNV1a 64 bit hash
            ulong hash = 14695981039346656037UL; //offset
            
            if (!_isFinished)
            {
                foreach (var (entType, _) in RegisteredTypes.OrderBy(kv => kv.Value.ClassId))
                {
                    //don't hash localonly types
                    if (entType.GetCustomAttribute<LocalOnly>() != null)
                        continue;
                    foreach (var field in entType.GetFields(FieldsFlags))
                    {
                        if (field.FieldType.IsSubclassOf(typeof(SyncableField)))
                        {
                            foreach (var syncableField in field.FieldType.GetFields(FieldsFlags))
                                TryHashField(syncableField);
                        }
                        else
                        {
                            TryHashField(field);
                        }
                    }
                }
                _isFinished = true;
            }
            return hash;
            
            void TryHashField(FieldInfo fi)
            {
                var ft = fi.FieldType;
                if ((fi.IsStatic && EntityClassData.IsRemoteCallType(ft)) || 
                    (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>)))
                {
                    string ftName = ft.Name + (ft.IsGenericType ? ft.GetGenericArguments()[0].Name : string.Empty);
                    for (int i = 0; i < ftName.Length; i++)
                    {
                        hash ^= ftName[i];
                        hash *= 1099511628211UL; //prime
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
            EntityClassInfo<TEntity>.ClassId = classId;
            RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(classId, constructor));
            MaxId = Math.Max(MaxId, classId);
            return this;
        }
    }
}