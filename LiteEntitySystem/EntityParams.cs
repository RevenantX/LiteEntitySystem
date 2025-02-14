namespace LiteEntitySystem
{
    public readonly struct EntityDataHeader
    {
        public readonly ushort Id;
        public readonly ushort ClassId;
        public readonly byte Version;
        public readonly int UpdateOrder;
        
        public EntityDataHeader(ushort id, ushort classId, byte version, int updateOrder)
        {
            Id = id;
            ClassId = classId;
            Version = version;
            UpdateOrder = updateOrder;
        }
    }
    
    public readonly ref struct EntityParams
    {
        public readonly EntityDataHeader Header;
        public readonly EntityManager EntityManager;
        
        internal readonly byte[] IOBuffer;

        internal EntityParams(EntityDataHeader dataHeader, EntityManager entityManager, byte[] ioBuffer)
        {
            Header = dataHeader;
            EntityManager = entityManager;
            IOBuffer = ioBuffer;
        }
    }
}