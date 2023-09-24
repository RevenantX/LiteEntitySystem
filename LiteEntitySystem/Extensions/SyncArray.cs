using System;

namespace LiteEntitySystem.Extensions
{
    public class SyncArray<T> : SyncableField where T : unmanaged
    {
        private struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        private T[] _data;
        private RemoteCall<SetCallData> _setRpcAction;
        private RemoteCall<int> _resizeRpcAction;
        private RemoteCallSpan<T> _initArrayAction;
        private RemoteCall _clearAction;
        
        public int Length => _data.Length;

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

        protected override void OnSyncRequested()
        {
            ExecuteRPC(_initArrayAction, _data);
        }

        protected override void RegisterRPC(in SyncableRPCRegistrator r)
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