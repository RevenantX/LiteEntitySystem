using System;
using System.Runtime.InteropServices;

namespace LiteEntitySystem
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct EntitySharedReference
    {
        public readonly ushort Id;
        public readonly byte Version;

        public bool IsInvalid => Id == EntityManager.InvalidEntityId;
        public bool IsLocal => Id >= EntityManager.MaxSyncedEntityCount;

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

        /// <summary>
        /// Create entity reference from byte data (3 bytes)
        /// </summary>
        /// <param name="data"></param>
        public EntitySharedReference(ReadOnlySpan<byte> data)
        {
            Id = (ushort)(data[0] << 8 | data[1]);
            Version = data[2];
        }

        public void WriteAsBytes(Span<byte> data)
        {
            data[0] = (byte)(Id >> 8);
            data[1] = (byte)Id;
            data[3] = Version;
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
    }
}