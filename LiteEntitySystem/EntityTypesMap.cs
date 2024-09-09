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
        private ulong _resultHash = 14695981039346656037UL; //FNV1a offset
        
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
                    .OrderBy(kv => kv.Value.ClassId)
                    .Where(kv =>
                    {
                        var attr = kv.Key.GetCustomAttribute<EntityFlagsAttribute>(true);
                        return attr == null || !attr.Flags.HasFlagFast(EntityFlags.LocalOnly);
                    }))
                {
                    var allTypesStack = Utils.GetBaseTypes(entType, typeof(InternalEntity), true);
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
            EntityClassInfo<TEntity>.ClassId = classId;
            RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(classId, constructor));
            MaxId = Math.Max(MaxId, classId);
            return this;
        }
    }
}