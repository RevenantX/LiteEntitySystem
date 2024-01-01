using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public unsafe ref struct MakeDiffData
    {
        internal int Position;
        public readonly bool IsOwned;
        private int _index;
        private readonly BitSpan _bitFlags;
        private byte* _sourceData;
        private readonly byte* _destData;
        private readonly ushort[] _fieldChangeTicks;
        private readonly ushort _playerTick;
            
        public MakeDiffData(
            BitSpan bitFlags, 
            ushort[] fieldChangeTicks,
            ushort playerTick,
            byte* sourceData, 
            byte* destData, 
            bool isOwned)
        {
            _bitFlags = bitFlags;
            _bitFlags.Clear();
            _fieldChangeTicks = fieldChangeTicks;
            _playerTick = playerTick;
            _index = -1;
            _sourceData = sourceData;
            _destData = destData;
            Position = 0;
            IsOwned = isOwned;
        }

        public void Write<T>(bool skip) where T : unmanaged
        {
            _index++;
            if (skip || Helpers.SequenceDiff(_fieldChangeTicks[_index], _playerTick) <= 0)
            {
                //Logger.Log($"SkipOld: {field.Name}");
                //old data
                _sourceData += sizeof(T);
                return;
            }
            _bitFlags.SetBit(_index);
            Unsafe.CopyBlock(_destData + Position, _sourceData + Position, (uint)sizeof(T));
            Position += sizeof(T);
        }
    }
}