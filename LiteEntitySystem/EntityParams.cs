namespace LiteEntitySystem
{
    public readonly struct EntityParams
    {
        public readonly ushort ClassId;
        public readonly ushort Id;
        public readonly byte Version;
        public readonly EntityManager EntityManager;

        internal EntityParams(
            ushort classId,
            ushort id,
            byte version,
            EntityManager entityManager)
        {
            ClassId = classId;
            Id = id;
            Version = version;
            EntityManager = entityManager;
        }
    }
}