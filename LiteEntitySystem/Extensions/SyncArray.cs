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
        private static RemoteCall<SetCallData> _setRpcAction;
        private static RemoteCall<int> _resizeRpcAction;
        private static RemoteCallSpan<T> _initArrayAction;
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

        protected internal override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetValueRPC, ref _setRpcAction);
            r.CreateClientAction(this, InitArrayRPC, ref _initArrayAction);
            r.CreateClientAction(this, Resize, ref _resizeRpcAction);
            r.CreateClientAction(this, Clear, ref _clearAction);
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