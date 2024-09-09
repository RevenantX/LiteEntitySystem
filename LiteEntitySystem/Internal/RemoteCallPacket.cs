namespace LiteEntitySystem.Internal
{
    internal struct RPCHeader
    {
        public ushort Id;
        public ushort Tick;
        public ushort ByteCount;
    }
    
    internal sealed class RemoteCallPacket
    {
        public RPCHeader Header;
        public byte[] Data;
        public ExecuteFlags Flags;
        public RemoteCallPacket Next;
        public int TotalSize => Header.ByteCount;
        
        public void Init(ushort tick, ushort typeSize, ushort rpcId, ExecuteFlags flags)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Flags = flags;
            Header.ByteCount = typeSize;
            Utils.ResizeOrCreate(ref Data, typeSize);
        }
    }
}