namespace LiteEntitySystem
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

        public void Setup(byte id, byte fieldId, ExecuteFlags flags, ushort tick, int size)
        {
            Id = id;
            FieldId = fieldId;
            Tick = tick;
            Size = (ushort)size;
            Utils.ResizeOrCreate(ref Data, size);
            Next = null;
            Flags = flags;
        }
    }
}