using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public readonly ref struct EntityParams
    {
        public readonly EntityDataHeader Header;
        public readonly EntityManager EntityManager;
        public readonly byte[] IOBuffer;

        internal EntityParams(EntityDataHeader dataHeader, EntityManager entityManager, byte[] ioBuffer)
        {
            Header = dataHeader;
            EntityManager = entityManager;
            IOBuffer = ioBuffer;
        }
    }
}