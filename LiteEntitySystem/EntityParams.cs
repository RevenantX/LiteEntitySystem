using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public readonly ref struct EntityParams
    {
        public readonly ushort ClassId;
        public readonly ushort Id;
        public readonly byte Version;
        public readonly int CreationTime;
        public readonly EntityManager EntityManager;

        internal EntityParams(EntityDataHeader dataHeader, EntityManager entityManager)
        {
            ClassId = dataHeader.ClassId;
            Id = dataHeader.Id;
            Version = dataHeader.Version;
            CreationTime = dataHeader.CreationTick;
            EntityManager = entityManager;
        }
        
        internal EntityParams(
            ushort classId,
            ushort id,
            byte version,
            int creationTime,
            EntityManager entityManager)
        {
            ClassId = classId;
            Id = id;
            Version = version;
            CreationTime = creationTime;
            EntityManager = entityManager;
        }
    }
}