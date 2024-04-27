using System;
using System.Runtime.CompilerServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Collections
{
    public class SequenceBinaryHeap<T>
    {
        private struct SequenceHeapNode
        {
            public T Data;
            public ushort Sequence;
        }
        
        private readonly SequenceHeapNode[] _data;
        private int _count;
        
        public int Count => _count;
        
        public SequenceBinaryHeap(int capacity) 
        {
            _data = new SequenceHeapNode[capacity];
            _count = 0;
        }
        
        public void Add(T item, ushort sequence) 
        {
            if (_count == _data.Length)
                throw new Exception("Heap capacity exceeded");

            // Add the item to the heap in the end position of the array (i.e. as a leaf of the tree)
            int position = _count++;
            _data[position] = new SequenceHeapNode { Data = item, Sequence = sequence };
            MoveUp(position);
        }
        
        public T ExtractMin()
        {
            var minNode = _data[0];
            (_data[0], _data[_count - 1]) = (_data[_count - 1], _data[0]);
            _count--;
            MoveDown(0);
            return minNode.Data;
        }
        
        private void MoveUp(int position)
        {
            while (position > 0 && Utils.SequenceDiff(_data[Parent(position)].Sequence, _data[position].Sequence) > 0)
            {
                int originalParentPos = Parent(position);
                (_data[position], _data[originalParentPos]) = (_data[originalParentPos], _data[position]);
                position = originalParentPos;
            }
        }

        private void MoveDown(int position)
        {
            while (true)
            {
                int lchild = LeftChild(position);
                int rchild = RightChild(position);
                int largest = lchild < _count && Utils.SequenceDiff(_data[lchild].Sequence, _data[position].Sequence) < 0 ? lchild : position;
                if (rchild < _count && Utils.SequenceDiff(_data[rchild].Sequence, _data[largest].Sequence) < 0)
                {
                    largest = rchild;
                }

                if (largest != position)
                {
                    (_data[position], _data[largest]) = (_data[largest], _data[position]);
                    position = largest;
                    continue;
                }

                break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Parent(int position) => (position - 1) / 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LeftChild(int position) => 2 * position + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RightChild(int position) => 2 * position + 2;
    }
}