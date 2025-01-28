using System.Runtime.InteropServices;

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
        public byte Tickrate;
        public int OriginalLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastPartData
    {
        public ushort Mtu;
        public ushort LastProcessedTick;
        public ushort LastReceivedTick;
        public byte BufferedInputsCount;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiffPartHeader
    {
        public byte UserHeader;
        public byte PacketType;
        public byte Part;
        public ushort Tick;
    }
    
    public struct InputPacketHeader
    {
        public ushort StateA;
        public ushort StateB;
        public float LerpMsec;
        public static readonly unsafe int Size = sizeof(InputPacketHeader);
    }

    internal static class InternalPackets
    {
        public const byte DiffSync = 1;
        public const byte ClientInput = 2;
        public const byte BaselineSync = 3;
        public const byte DiffSyncLast = 4;
        public const byte ClientRequest = 5;
        public const byte ClientRPC = 6;
    }
}