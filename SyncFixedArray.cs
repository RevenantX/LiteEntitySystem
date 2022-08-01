using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public class SyncFixedArray<T> : SyncableField where T : struct
    {
        public struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        private readonly T[] _data;
        private Action<SetCallData> _setRpcAction;

        public readonly int Length;

        public SyncFixedArray(int size)
        {
            Length = size;
            _data = new T[size];
        }

        public override void OnServerInitialized()
        {
            CreateClientAction(SetValueRPC, out _setRpcAction);
        }

        [SyncableRemoteCall]
        private void SetValueRPC(SetCallData setCallData)
        {
            _data[setCallData.Index] = setCallData.Value;
        }
        
        public T this[int index]
        {
            get => _data[index];
            set
            {
                _data[index] = value;
                _setRpcAction?.Invoke(new SetCallData { Value = value, Index = (ushort)index });
            }
        }

        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            byte[] byteData = Unsafe.As<byte[]>(_data);
            int bytesCount = Unsafe.SizeOf<T>() * _data.Length;
            fixed(void* rawData = byteData)
                Unsafe.CopyBlock(data + position, rawData, (uint)bytesCount);
            position += bytesCount;
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            byte[] byteData = Unsafe.As<byte[]>(_data);
            int bytesCount = Unsafe.SizeOf<T>() * _data.Length;
            fixed(void* rawData = byteData)
                Unsafe.CopyBlock(rawData, data + position, (uint)bytesCount);
            position += bytesCount;
        }
    }
}