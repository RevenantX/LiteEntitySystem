namespace LiteEntitySystem.Extensions
{
    public class SyncPongTimer : SyncableField
    {
        public float MaxTime => _maxTime;
        public float ElapsedTime => _time;
        public bool IsTimeElapsed => _time >= _maxTime;
        public float CountdownTime => _maxTime - _time;
        public bool HasStarted => _time > 0;

        private SyncVar<float> _time;
        private const float _maxTime = 1f;

        public SyncPongTimer() { }

        public float Progress => _time;

        public void Reset()
        {
            _time.Value = 0f;
        }

        public void Finish()
        {
            _time.Value = _maxTime;
        }

        public bool UpdateForward(float delta)
        {
            if (delta > 0f)
            {
                float newTime = _time.Value + delta;
                if (newTime > 1f) newTime = 1f;
                _time.Value = newTime;
            }
            return IsTimeElapsed;
        }

        public bool UpdateBackward(float delta)
        {
            if (delta > 0f)
            {
                float newTime = _time.Value - delta;
                if (newTime < 0f) newTime = 0f;
                _time.Value = newTime;
            }
            return !HasStarted;
        }
    }
}