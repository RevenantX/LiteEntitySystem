using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Extensions
{
    public class SyncFixedArray<T> : SyncableField where T : unmanaged
    {
        public struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        public readonly T[] Data;
        private RemoteCall<SetCallData> _setRpcAction;

        public readonly int Length;

        public SyncFixedArray(int size)
        {
            Length = size;
            Data = new T[size];
        }

        public override void RegisterRPC(in SyncableRPCRegistrator registrator)
        {
            registrator.CreateClientAction(this, SetValueRPC, ref _setRpcAction);
        }
        
        private void SetValueRPC(SetCallData setCallData)
        {
            Data[setCallData.Index] = setCallData.Value;
        }
        
        public T this[int index]
        {
            get => Data[index];
            set
            {
                Data[index] = value;
                ExecuteRPC(_setRpcAction, new SetCallData { Value = value, Index = (ushort)index });
            }
        }

        public override unsafe int GetFullSyncSize()
        {
            return sizeof(T) * Length;
        }

        public override unsafe void FullSyncWrite(ServerEntityManager server, Span<byte> dataSpan)
        {
            byte[] byteData = Unsafe.As<byte[]>(Data);
            fixed(byte* rawData = byteData, data = dataSpan)
                Unsafe.CopyBlock(data, rawData, (uint)dataSpan.Length);
        }

        public override unsafe void FullSyncRead(ClientEntityManager client, ReadOnlySpan<byte> dataSpan)
        {
            byte[] byteData = Unsafe.As<byte[]>(Data);
            fixed(byte* rawData = byteData, data = dataSpan)
                Unsafe.CopyBlock(rawData, data, (uint)dataSpan.Length);
        }
    }
}