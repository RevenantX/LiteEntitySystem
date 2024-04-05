using System;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public class IdGeneratorByte
    {
        private readonly byte _maxValue;
        private readonly Queue<byte> _queue = new();
        private byte _counter;

        public byte GetNewId()
        {
            if (_queue.Count > 0)
                return _queue.Dequeue();
            if (_counter == _maxValue)
                throw new Exception("IdGenerator overflow");
            return _counter++;
        }
        
        public int AvailableIds => _maxValue - _counter + _queue.Count;

        public void ReuseId(byte id)
        {
            _queue.Enqueue(id);
        }

        public IdGeneratorByte(byte initialValue, byte maxValue)
        {
            _counter = initialValue;
            _maxValue = maxValue;
        }
        
        public void Reset()
        {
            _queue.Clear();
            _counter = 0;
        }
    }
    
    public class IdGeneratorUShort
    {
        private readonly ushort _maxValue;
        private readonly Queue<ushort> _queue = new();
        private ushort _counter;

        public ushort GetNewId()
        {
            if (_queue.Count > 0)
                return _queue.Dequeue();
            if (_counter == _maxValue)
                throw new Exception("IdGenerator overflow");
            return _counter++;
        }
        
        public int AvailableIds => _maxValue - _counter + _queue.Count;

        public void ReuseId(ushort id)
        {
            _queue.Enqueue(id);
        }

        public IdGeneratorUShort(ushort initialValue, ushort maxValue)
        {
            _counter = initialValue;
            _maxValue = maxValue;
        }
        
        public void Reset()
        {
            _queue.Clear();
            _counter = 0;
        }
    }
}