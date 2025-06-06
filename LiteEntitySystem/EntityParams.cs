namespace LiteEntitySystem
{
    public readonly struct EntityDataHeader
    {
        public readonly ushort ClassId;
        public readonly byte Version;
        public readonly int UpdateOrder;
        
        public EntityDataHeader(ushort classId, byte version, int updateOrder)
        {
            ClassId = classId;
            Version = version;
            UpdateOrder = updateOrder;
        }
    }
    
    public readonly ref struct EntityParams
    {
        public readonly ushort Id;
        public readonly EntityDataHeader Header;
        public readonly EntityManager EntityManager;
        
        internal readonly byte[] IOBuffer;

        internal EntityParams(ushort id, EntityDataHeader dataHeader, EntityManager entityManager, byte[] ioBuffer)
        {
            Id = id;
            Header = dataHeader;
            EntityManager = entityManager;
            IOBuffer = ioBuffer;
        }
    }
}