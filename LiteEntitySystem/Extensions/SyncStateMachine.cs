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

        public T CurrentState => _state;

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
            _state.Value = state;
            _data[_state.GetEnumValue()].OnEnter?.Invoke();
        }

        public void ChangeState(T state)
        {
            _data[_state.GetEnumValue()].OnExit?.Invoke();
            _state.Value = state;
            _data[_state.GetEnumValue()].OnEnter?.Invoke();
        }

        public void Update(float dt)
        {
            _data[_state.GetEnumValue()].OnUpdate?.Invoke(dt);
        }
    }
}