namespace LiteEntitySystem.Internal
{
    internal sealed class RemoteCallPacket
    {
        public byte Id = byte.MaxValue;
        public byte FieldId = byte.MaxValue;
        public ushort Tick;
        public byte[] Data;
        public ushort Size;
        public ExecuteFlags Flags;
        public RemoteCallPacket Next;

        public void Init(ushort tick, ushort dataSize, byte rpcId, ExecuteFlags flags)
        {
            Tick = tick;
            Id = rpcId;
            FieldId = byte.MaxValue;
            Flags = flags;
            Size = dataSize;
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(ushort tick, ushort dataSize, byte rpcId, ExecuteFlags flags, int count)
        {
            Tick = tick;
            Id = rpcId;
            FieldId = byte.MaxValue;
            Flags = flags;
            Size = (ushort)(dataSize*count);
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(ushort tick, ushort dataSize, SyncableRemoteCall rc, byte fieldId)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = fieldId;
            Size = dataSize;
            Utils.ResizeOrCreate(ref Data, Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
        
        public void Init(ushort tick, ushort dataSize, SyncableRemoteCall rc, byte fieldId, int count)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = fieldId;
            Size = (ushort)(dataSize * count);
            Utils.ResizeOrCreate(ref Data, Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
    }
}