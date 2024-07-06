namespace LiteEntitySystem.Internal
{
    internal struct RPCHeader
    {
        public ushort Id;
        public ushort Tick;
        public ushort ByteCount1;
        public ushort ByteCount2;
    }
    
    internal sealed class RemoteCallPacket
    {
        public RPCHeader Header;
        public byte[] Data;
        public ExecuteFlags Flags;
        public RemoteCallPacket Next;
        public int TotalSize => Header.ByteCount1 + Header.ByteCount2;
        
        public void Init(ushort tick, ushort typeSize1, ushort typeSize2, ushort rpcId, ExecuteFlags flags)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Flags = flags;
            Header.ByteCount1 = typeSize1;
            Header.ByteCount2 = typeSize2;
            Utils.ResizeOrCreate(ref Data, typeSize1+typeSize2);
        }
    }
}