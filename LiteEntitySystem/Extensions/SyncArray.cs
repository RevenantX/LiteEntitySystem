using System;

namespace LiteEntitySystem.Extensions
{
    public partial class SyncArray<T> : SyncableField where T : unmanaged
    {
        private struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        private T[] _data;
        [BindRpc(nameof(SetValueRPC))]
        private static RemoteCall<SetCallData> _setRpcAction;
        [BindRpc(nameof(Resize))]
        private static RemoteCall<int> _resizeRpcAction;
        [BindRpc(nameof(InitArrayRPC))]
        private static RemoteCallSpan<T> _initArrayAction;
        [BindRpc(nameof(Clear))]
        private static RemoteCall _clearAction;
        
        public int Length => _data.Length;

        /// <summary>
        /// Changes to this array will NOT sync,
        /// so it should be used as readonly!
        /// </summary>
        public T[] Value => _data;

        public SyncArray(int size)
        {
            _data = new T[size];
        }

        public void Resize(int newSize)
        {
            if(_data.Length != newSize)
                Array.Resize(ref _data, newSize);
            ExecuteRPC(_resizeRpcAction, newSize);
        }

        public void Clear()
        {
            Array.Clear(_data, 0, _data.Length);
            ExecuteRPC(_clearAction);
        }

        protected internal override void OnSyncRequested()
        {
            ExecuteRPC(_initArrayAction, _data);
        }

        private void InitArrayRPC(ReadOnlySpan<T> inData)
        {
            if (inData.Length != _data.Length)
                _data = new T[inData.Length];
            inData.CopyTo(_data);
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