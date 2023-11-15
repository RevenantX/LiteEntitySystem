using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Extensions
{
    public partial class SyncTimer : SyncableField
    {
        public float MaxTime => _maxTime;
        public float ElapsedTime => _time;
        public bool IsTimeElapsed => _time >= _maxTime;
        public float CountdownTime => _maxTime - _time;
        public bool HasStarted => ElapsedTime > 0;
        
        private SyncVar<float> _time;
        private SyncVar<float> _maxTime;

        public SyncTimer(float maxTime) 
        {
            _maxTime = maxTime;
            Finish();
        }

        public SyncTimer() 
        {
            
        }

        public float Progress
        {
            get
            {
                float p = _time/_maxTime;
                return p > 1f ? 1f : p;
            }
        }

        public void Reset()
        {
            _time = 0f;
        }

        public void Reset(float maxTime)
        {
            _maxTime = maxTime;
            _time = 0f;
        }

        public void Finish()
        {
            _time = _maxTime;
        }

        public float LerpByProgress(float a, float b)
        {
            return Helpers.Lerp(a, b, Progress);
        }

        public float LerpByProgress(float a, float b, bool inverse)
        {
            return inverse
                ? Helpers.Lerp(a, b, Progress)
                : Helpers.Lerp(b, a, Progress);
        }

        public bool UpdateAndCheck(float delta)
        {
            if (IsTimeElapsed)
                return false;
            return Update(delta);
        }

        public bool Update(float delta)
        {
            if (_time < _maxTime)
                _time += delta;
            return IsTimeElapsed;
        }

        public bool CheckAndSubtractMaxTime()
        {
            if (_time >= _maxTime)
            {
                _time -= _maxTime;
                return true;
            }
            return false;
        }

        public bool UpdateAndReset(float delta)
        {
            if (Update(delta))
            {
                _time -= _maxTime;
                return true;
            }
            return false;
        }
    }
}