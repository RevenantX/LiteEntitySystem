using System;

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
        private RemoteCallSpan<T> _initArrayAction;

        public readonly int Length;

        public SyncFixedArray(int size)
        {
            Length = size;
            Data = new T[size];
        }

        protected override void OnSyncRequested()
        {
            ExecuteRPC(_initArrayAction, Data);
        }

        protected override void RegisterRPC(in SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetValueRPC, ref _setRpcAction);
            r.CreateClientAction(this, InitArrayRPC, ref _initArrayAction);
        }

        private void InitArrayRPC(ReadOnlySpan<T> data)
        {
            data.CopyTo(Data);
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
    }
}