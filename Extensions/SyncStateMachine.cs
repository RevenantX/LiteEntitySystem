using System;
using System.Runtime.CompilerServices;

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
        [SyncableSyncVar]
        private T _state;

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

        private ref StateCalls GetCurrentState()
        {
            return ref _data[_state.GetEnumValue()];
        }

        public void SetInitialState(T state)
        {
            _state = state;
            GetCurrentState().OnEnter?.Invoke();
        }

        public void ChangeState(T state)
        {
            GetCurrentState().OnExit?.Invoke();
            _state = state;
            GetCurrentState().OnEnter?.Invoke();
        }

        public void Update(float dt)
        {
            GetCurrentState().OnUpdate?.Invoke(dt);
        }

        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            Unsafe.Write(data + position, _state);
            position += Unsafe.SizeOf<T>();
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            _state = Unsafe.Read<T>(data + position);
            position += Unsafe.SizeOf<T>();
        }
    }
}