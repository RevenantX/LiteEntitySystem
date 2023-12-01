using System;

namespace LiteEntitySystem.Extensions
{
    public partial class SyncFixedArray<T> : SyncableField where T : unmanaged
    {
        private struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        private readonly T[] _data;
        [BindRpc(nameof(SetValueRPC))]
        private static RemoteCall<SetCallData> _setRpcAction;
        [BindRpc(nameof(InitArrayRPC))]
        private static RemoteCallSpan<T> _initArrayAction;

        public readonly int Length;

        public SyncFixedArray(int size)
        {
            Length = size;
            _data = new T[size];
        }

        protected internal override void OnSyncRequested()
        {
            ExecuteRPC(_initArrayAction, _data);
        }

        private void InitArrayRPC(ReadOnlySpan<T> data)
        {
            data.CopyTo(_data);
        }
        
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
                ExecuteRPC(_setRpcAction, new SetCallData { Value = value, Index = (ushort)index });
            }
        }
    }
}