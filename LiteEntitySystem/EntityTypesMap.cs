using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private readonly SHA256 _sha256 = SHA256.Create();
        private bool _isFinished;
        private const BindingFlags FieldsFlags = BindingFlags.Instance |
                                                 BindingFlags.Public |
                                                 BindingFlags.NonPublic |
                                                 BindingFlags.Static;
        
        /// <summary>
        /// Can be used to detect that server/client has difference
        /// </summary>
        /// <returns>hash</returns>
        public byte[] EvaluateEntityClassDataHash()
        {
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
                _sha256.TransformFinalBlock(new byte[]{ 255 }, 0, 1);
                _isFinished = true;
            }
            
            return _sha256.Hash;
            
            void TryHashField(FieldInfo fi)
            {
                var ft = fi.FieldType;
                if ((fi.IsStatic && EntityClassData.IsRemoteCallType(ft)) || 
                    (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>)))
                {
                    byte[] ftName = Encoding.ASCII.GetBytes(ft.Name + (ft.IsGenericType ? ft.GetGenericArguments()[0].Name : string.Empty));
                    _sha256.TransformBlock(ftName, 0, ftName.Length, null, 0);
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