using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SyncEntityReference
    {
        public const int DataSize = 3; //Id 2, Version 1
        
        public ushort Id;
        public byte Version;
        internal byte FieldId;
        
        public bool IsInvalid => Id == EntityManager.InvalidEntityId;
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

        public SyncEntityReference(InternalEntity entity)
        {
            FieldId = 0;
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

        public static bool operator ==(SyncEntityReference obj1, SyncEntityReference obj2)
        {
            return obj1.Id == obj2.Id && obj1.Version == obj2.Version;
        }
        
        public static bool operator !=(SyncEntityReference obj1, SyncEntityReference obj2)
        {
            return obj1.Id != obj2.Id || obj1.Version != obj2.Version;
        }

        public override bool Equals(object obj)
        {
            if (obj is SyncEntityReference esr)
                return esr.Id == Id && esr.Version == Version;
            return false;
        }

        public override int GetHashCode()
        {
            return Id + Version * ushort.MaxValue;
        }

        public static implicit operator SyncEntityReference(InternalEntity entity)
        {
            return new SyncEntityReference(entity);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InternalEntityReference
    {
        public ushort Id;
        public byte Version;
        
        public static implicit operator SyncEntityReference(InternalEntityReference i)
        {
            return new SyncEntityReference
            {
                Id = i.Id,
                Version = i.Version
            };
        }
    }
}