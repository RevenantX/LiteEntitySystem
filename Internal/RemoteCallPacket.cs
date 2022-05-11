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

        public void Init(RemoteCall rc)
        {
            Id = rc.Id;
            FieldId = byte.MaxValue;
            Flags = rc.Flags;
            Size = (ushort)rc.DataSize;
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(RemoteCall rc, int count)
        {
            Id = rc.Id;
            FieldId = byte.MaxValue;
            Flags = rc.Flags;
            Size = (ushort)(rc.DataSize*count);
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(SyncableRemoteCall rc, byte fieldId)
        {
            Id = rc.Id;
            FieldId = fieldId;
            Size = (ushort)rc.DataSize;
            Utils.ResizeOrCreate(ref Data, Size);
        }
        
        public void Init(SyncableRemoteCall rc, byte fieldId, int count)
        {
            Id = rc.Id;
            FieldId = fieldId;
            Size = (ushort)(rc.DataSize * count);
            Utils.ResizeOrCreate(ref Data, Size);
        }
    }
}