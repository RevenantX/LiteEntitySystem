namespace LiteEntitySystem.Internal
{
    internal struct RPCHeader
    {
        public byte Id;
        public byte FieldId;
        public ushort Tick;
        public ushort Size;
    }
    
    internal sealed class RemoteCallPacket
    {
        public RPCHeader Header;
        public byte[] Data;
        public ExecuteFlags Flags;
        public RemoteCallPacket Next;

        public void Init(ushort tick, ushort dataSize, byte rpcId, ExecuteFlags flags)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Header.FieldId = byte.MaxValue;
            Flags = flags;
            Header.Size = dataSize;
            Utils.ResizeOrCreate(ref Data, Header.Size);
        }
        
        public void Init(ushort tick, ushort dataSize, byte rpcId, ExecuteFlags flags, int count)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Header.FieldId = byte.MaxValue;
            Flags = flags;
            Header.Size = (ushort)(dataSize*count);
            Utils.ResizeOrCreate(ref Data, Header.Size);
        }
        
        public void Init(ushort tick, ushort dataSize, byte rpcId, byte fieldId)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Header.FieldId = fieldId;
            Header.Size = dataSize;
            Utils.ResizeOrCreate(ref Data, Header.Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
        
        public void Init(ushort tick, ushort dataSize, byte rpcId, byte fieldId, int count)
        {
            Header.Tick = tick;
            Header.Id = rpcId;
            Header.FieldId = fieldId;
            Header.Size = (ushort)(dataSize * count);
            Utils.ResizeOrCreate(ref Data, Header.Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
    }
}