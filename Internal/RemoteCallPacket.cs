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

        public void Init(ushort tick, RemoteCall rc)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = byte.MaxValue;
            Flags = rc.Flags;
            Size = (ushort)rc.DataSize;
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(ushort tick, RemoteCall rc, int count)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = byte.MaxValue;
            Flags = rc.Flags;
            Size = (ushort)(rc.DataSize*count);
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(ushort tick, SyncableRemoteCall rc, byte fieldId)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = fieldId;
            Size = (ushort)rc.DataSize;
            Utils.ResizeOrCreate(ref Data, Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
        
        public void Init(ushort tick, SyncableRemoteCall rc, byte fieldId, int count)
        {
            Tick = tick;
            Id = rc.Id;
            FieldId = fieldId;
            Size = (ushort)(rc.DataSize * count);
            Utils.ResizeOrCreate(ref Data, Size);
            Flags = ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
        }
    }
}