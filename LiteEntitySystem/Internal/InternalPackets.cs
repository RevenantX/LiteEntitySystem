using System.Runtime.InteropServices;
using LiteNetLib;

namespace LiteEntitySystem.Internal
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BaselineDataHeader
    {
        public byte UserHeader;
        public byte PacketType;
        public byte PlayerId;
        public byte SendRate;
        public ushort Tick;
        public int OriginalLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FirstPartHeader
    {
        public byte UserHeader;
        public byte PacketType;
        public byte Part;
        public ushort Tick;
        public ushort LastProcessedTick;
        public ushort LastReceivedTick;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiffPartHeader
    {
        public byte UserHeader;
        public byte PacketType;
        public byte Part;
        public ushort Tick;
    }
}