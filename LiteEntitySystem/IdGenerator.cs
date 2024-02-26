using System;
using System.Collections.Generic;

namespace LiteEntitySystem
{
    public class IdGeneratorByte
    {
        private readonly byte _maxValue;
        private readonly Queue<byte> _queue = new();
        private byte _counter;

        public byte GetNewId() => _queue.Count > 0 ? _queue.Dequeue() : IncrementCounter();
        public int AvailableIds => _maxValue - _counter + _queue.Count;

        public void ReuseId(byte id)
        {
            _queue.Enqueue(id);
        }

        public IdGeneratorByte() : this(byte.MaxValue) {}

        public IdGeneratorByte(byte maxValue)
        {
            _maxValue = maxValue;
        }
        
        public void Reset()
        {
            _queue.Clear();
            _counter = 0;
        }
        
        private byte IncrementCounter()
        {
            if (_counter == _maxValue)
                throw new Exception("IdGenerator overflow");
            return _counter++;
        }
    }
    
    public class IdGeneratorUShort
    {
        private readonly ushort _maxValue;
        private readonly Queue<ushort> _queue = new();
        private ushort _counter;

        public ushort GetNewId() => _queue.Count > 0 ? _queue.Dequeue() : IncrementCounter();
        public int AvailableIds => _maxValue - _counter + _queue.Count;

        public void ReuseId(ushort id)
        {
            _queue.Enqueue(id);
        }

        public IdGeneratorUShort() : this(ushort.MaxValue) {}

        public IdGeneratorUShort(ushort maxValue)
        {
            _maxValue = maxValue;
        }
        
        public void Reset()
        {
            _queue.Clear();
            _counter = 0;
        }
        
        private ushort IncrementCounter()
        {
            if (_counter == _maxValue)
                throw new Exception("IdGenerator overflow");
            return _counter++;
        }
    }
}