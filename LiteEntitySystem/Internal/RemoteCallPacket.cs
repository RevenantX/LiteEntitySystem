using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    internal struct RPCHeader
    {
        public ushort EntityId;
        public ushort Id;
        public ushort Tick;
        public ushort ByteCount;
    }
    
    internal sealed class RemoteCallPacket
    {
        public RPCHeader Header;
        public byte[] Data;
        public NetPlayer OnlyForPlayer;
        public ExecuteFlags ExecuteFlags;
        
        public const int ReserverdRPCsCount = 3;
        public const ushort NewRPCId = 0;
        public const ushort ConstructRPCId = 1;
        public const ushort DestroyRPCId = 2;
        
        public static void InitReservedRPCs(List<RpcFieldInfo> rpcCahce)
        {
            for(int i = 0; i < ReserverdRPCsCount; i++)
                rpcCahce.Add(new RpcFieldInfo(-1, null));
        }

        public unsafe void WriteTo(byte* resultData, ref int position)
        {
            *(RPCHeader*)(resultData + position) = Header;
            fixed (byte* rpcData = Data)
                RefMagic.CopyBlock(resultData + sizeof(RPCHeader) + position, rpcData, Header.ByteCount);
            position += sizeof(RPCHeader) + Header.ByteCount;
        }
        
        public void Init(NetPlayer targetPlayer, InternalEntity entity, ushort tick, ushort byteCount, ushort rpcId, ExecuteFlags executeFlags)
        {
            OnlyForPlayer = targetPlayer;
            ExecuteFlags = executeFlags;
            Header.EntityId = entity.Id;
            Header.Tick = tick;
            Header.Id = rpcId;
            Header.ByteCount = byteCount;
            Utils.ResizeOrCreate(ref Data, byteCount);
        }
    }
}