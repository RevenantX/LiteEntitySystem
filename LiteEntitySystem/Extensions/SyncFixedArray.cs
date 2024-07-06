using System;

namespace LiteEntitySystem.Extensions
{
    public class SyncFixedArray<T> : SyncableField where T : unmanaged, IEquatable<T>
    {
        private struct SetCallData
        {
            public T Value;
            public ushort Index;
        }
        
        private readonly T[] _data;
        private static RemoteCall<SetCallData> _setRpcAction;
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

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetValueRPC, ref _setRpcAction);
            r.CreateClientAction(this, InitArrayRPC, ref _initArrayAction);
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
                if(_data[index].Equals(value))
                   return;
                _data[index] = value;
                ExecuteRPC(_setRpcAction, new SetCallData { Value = value, Index = (ushort)index });
            }
        }
    }
}