using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Extensions
{
    public class SyncFixedArray<T> : SyncableField where T : struct
    {
        public struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        public readonly T[] Data;
        private Action<SetCallData> _setRpcAction;

        public readonly int Length;

        public SyncFixedArray(int size)
        {
            Length = size;
            Data = new T[size];
        }

        public override void OnServerInitialized()
        {
            CreateClientAction(SetValueRPC, out _setRpcAction);
        }

        [SyncableRemoteCall]
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
                _setRpcAction?.Invoke(new SetCallData { Value = value, Index = (ushort)index });
            }
        }

        public override unsafe void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            byte[] byteData = Unsafe.As<byte[]>(Data);
            int bytesCount = Unsafe.SizeOf<T>() * Length;
            fixed(byte* rawData = byteData, data = dataSpan)
                Unsafe.CopyBlock(data + position, rawData, (uint)bytesCount);
            position += bytesCount;
        }

        public override unsafe void FullSyncRead(Span<byte> dataSpan, ref int position)
        {
            byte[] byteData = Unsafe.As<byte[]>(Data);
            int bytesCount = Unsafe.SizeOf<T>() * Length;
            fixed(byte* rawData = byteData, data = dataSpan)
                Unsafe.CopyBlock(rawData, data + position, (uint)bytesCount);
            position += bytesCount;
        }
    }
}