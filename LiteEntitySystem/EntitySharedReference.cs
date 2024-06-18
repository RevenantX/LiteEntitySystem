using System;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct EntitySharedReference : IEquatable<EntitySharedReference>
    {
        public readonly ushort Id;
        public readonly byte Version;

        public bool IsValid => Id != EntityManager.InvalidEntityId;
        public bool IsInvalid => Id == EntityManager.InvalidEntityId;
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        public static readonly EntitySharedReference Empty;

        public EntitySharedReference(ushort id, byte version)
        {
            Id = id;
            Version = version;
        }

        internal EntitySharedReference(ControllerLogic controllerLogic)
        {
            if (controllerLogic == null)
            {
                Id = EntityManager.InvalidEntityId;
                Version = 0;
            }
            else
            {
                Id = controllerLogic.Id;
                Version = controllerLogic.Version;
            }
        }

        public EntitySharedReference(EntityLogic entity)
        {
            if (entity == null)
            {
                Id = EntityManager.InvalidEntityId;
                Version = 0;
            }
            else
            {
                Id = entity.Id;
                Version = entity.Version;
            }
        }

        public static bool operator ==(EntitySharedReference obj1, EntitySharedReference obj2)
        {
            return obj1.Id == obj2.Id && obj1.Version == obj2.Version;
        }
        
        public static bool operator !=(EntitySharedReference obj1, EntitySharedReference obj2)
        {
            return obj1.Id != obj2.Id || obj1.Version != obj2.Version;
        }

        public override bool Equals(object obj)
        {
            if (obj is EntitySharedReference esr)
                return esr.Id == Id && esr.Version == Version;
            return false;
        }

        public override int GetHashCode()
        {
            return Id + Version * ushort.MaxValue;
        }

        public static implicit operator EntitySharedReference(EntityLogic entity)
        {
            return new EntitySharedReference(entity);
        }

        public override string ToString()
        {
            return $"Id: {Id}, Version: {Version}";
        }

        public bool Equals(EntitySharedReference other) =>
            other.Id == Id && other.Version == Version;
    }
}