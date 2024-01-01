using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public unsafe ref struct PreloadInterpolationData
    {
        internal int Position;
        private readonly BitReadOnlySpan _fieldBits;
        private readonly bool _isRemoteControlled;
        private byte* _interpolationData;
        private readonly byte* _soruceData;
        private int _index;

        public PreloadInterpolationData(BitReadOnlySpan fieldBits, bool isRemoteControlled, byte* sourceData, byte* interpolationData)
        {
            _fieldBits = fieldBits;
            _index = -1;
            Position = 0;
            _isRemoteControlled = isRemoteControlled;
            _soruceData = sourceData;
            _interpolationData = interpolationData;
        }

        public void Skip<T>() where T : unmanaged
        {
            _index++;
            if (_fieldBits[_index])
                Position += sizeof(T);
        }
            
        public void Preload<T>() where T : unmanaged
        {
            _index++;
            if (_fieldBits[_index])
            {
                if (_isRemoteControlled)
                    Unsafe.CopyBlock(_interpolationData, _soruceData + Position, (uint)sizeof(T));
                Position += sizeof(T);
            }
            _interpolationData += sizeof(T);
        }
    }
}