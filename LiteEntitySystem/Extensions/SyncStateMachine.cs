using System;

namespace LiteEntitySystem.Extensions
{
    public struct StateCalls
    {
        public Action OnEnter;
        public Action<float> OnUpdate;
        public Action OnExit;
    }
    
    public class SyncStateMachine<T> : SyncableField where T : unmanaged, Enum
    {
        private SyncVar<T> _state;

        public SyncVar<T> CurrentState => _state;

        private readonly StateCalls[] _data;

        public SyncStateMachine()
        {
            _data = new StateCalls[Enum.GetValues(typeof(T)).Length];
        }
        
        public SyncStateMachine<T> Add(T stateName, StateCalls stateCalls)
        {
            _data[stateName.GetEnumValue()] = stateCalls;
            return this;
        }

        public void SetInitialState(T state)
        {
            _state = state;
            _data[_state.GetEnumValue()].OnEnter?.Invoke();
        }

        public void ChangeState(T state)
        {
            _data[_state.GetEnumValue()].OnExit?.Invoke();
            _state = state;
            _data[_state.GetEnumValue()].OnEnter?.Invoke();
        }

        public void Update(float dt)
        {
            _data[_state.GetEnumValue()].OnUpdate?.Invoke(dt);
        }

        public override unsafe int GetFullSyncSize()
        {
            return sizeof(T);
        }

        public override unsafe void FullSyncWrite(ServerEntityManager server, Span<byte> dataSpan)
        {
            fixed(byte *data = dataSpan)
                *(T*)data = _state;
        }

        public override unsafe void FullSyncRead(ClientEntityManager client, ReadOnlySpan<byte> dataSpan)
        {
            fixed(byte *data = dataSpan)
                _state = *(T*)data;
        }
    }
}