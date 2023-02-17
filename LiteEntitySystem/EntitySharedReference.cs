using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct EntitySharedReference
    {
        public readonly ushort Id;
        public readonly byte Version;

        public bool IsInvalid => Id == EntityManager.InvalidEntityId;
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        public EntitySharedReference(InternalEntity entity)
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

        public static implicit operator EntitySharedReference(InternalEntity entity)
        {
            return new EntitySharedReference(entity);
        }
    }
}