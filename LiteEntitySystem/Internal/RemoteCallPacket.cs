namespace LiteEntitySystem.Internal
{
    internal struct RPCHeader
    {
        public ushort Id;
        public ushort Tick;
        public ushort TypeSize;
        public ushort Count;
    }
    
    internal sealed class RemoteCallPacket
    {
        public RPCHeader Header;
        public byte[] Data;
        public ExecuteFlags Flags;
        public RemoteCallPacket Next;
        public int TotalSize => Header.TypeSize * Header.Count;
        public bool OnSync;
        
        public void Init(ushort tick, ushort typeSize, ushort rpcId, ExecuteFlags flags, int count)
        {
            OnSync = false;
            Header.Tick = tick;
            Header.Id = rpcId;
            Flags = flags;
            Header.TypeSize = typeSize;
            Header.Count = (ushort)count;
            Utils.ResizeOrCreate(ref Data, typeSize*count);
        }
    }
}