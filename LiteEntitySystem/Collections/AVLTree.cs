using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Collections
{
    public class AVLTree<T> : IEnumerable<T> where T : IComparable<T>
    {
        public struct Enumerator : IEnumerator<T>
        {
            private readonly AVLTree<T> _tree;
            private readonly TreeNode[] _nodes;
            private int _index;
            private readonly int _count;
            
            public Enumerator(AVLTree<T> tree)
            {
                tree._enumeratorsCount++;
                _tree = tree;
                _nodes = tree._nodes;
                _index = -1;
                _count = tree._nodesCount;
            }
            
            public void Dispose() { _tree._enumeratorsCount--; }
            public bool MoveNext() => ++_index != _count;
            public void Reset() => _index = -1;
            public T Current => _nodes[_nodes[_index].SortedNextId].Data;
            object IEnumerator.Current => Current;
        }

        private struct TreeNode
        {
            public T Data;
            public int LeftId;
            public int RightId;
            public int Height;
            public int SortedNextId;
            public int IdStack;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetData(ref T data)
            {
                Data = data;
                Height = 1;
                LeftId = EmptyNode;
                RightId = EmptyNode;
            }
        }

        private const int InitialSize = 8;
        private const int EmptyNode = 0;
        private TreeNode[] _nodes = new TreeNode[InitialSize];

        //h <= 1,45*log(2, int.MaxValue(2147483647) + 2) <= 45
        private readonly int[] _nodesStack = new int[45];

        private int _root = EmptyNode;
        private int _nodesCount;
        private bool _isDirty;
        private int _idCounter = 1;
        private int _idStackCount;

        /// <summary>
        /// Elements count
        /// </summary>
        public int Count => _nodesCount;
        
        private int _enumeratorsCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UpdateHeightAndGetBalance(TreeNode[] nodes, ref TreeNode node)
        {
            int leftHeight = nodes[node.LeftId].Height;
            int rightHeight = nodes[node.RightId].Height;
            node.Height = (leftHeight > rightHeight ? leftHeight : rightHeight) + 1;
            return leftHeight - rightHeight;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetNodeBalance(TreeNode[] nodes, int id)
        {
            ref var node = ref nodes[id];
            return nodes[node.LeftId].Height - nodes[node.RightId].Height;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RightRotate(TreeNode[] nodes, int y)
        {
            ref var yNode = ref nodes[y];
            int x = yNode.LeftId;
            ref var xNode = ref nodes[x];
            yNode.LeftId = xNode.RightId;
            xNode.RightId = y;
            yNode.Height = Math.Max(nodes[yNode.LeftId].Height, nodes[yNode.RightId].Height) + 1;
            xNode.Height = Math.Max(nodes[xNode.LeftId].Height, nodes[xNode.RightId].Height) + 1;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LeftRotate(TreeNode[] nodes, int x)
        {
            ref var xNode = ref nodes[x];
            int y = xNode.RightId;
            ref var yNode = ref nodes[y];
            xNode.RightId = yNode.LeftId;
            yNode.LeftId = x;
            xNode.Height = Math.Max(nodes[xNode.LeftId].Height, nodes[xNode.RightId].Height) + 1;
            yNode.Height = Math.Max(nodes[yNode.LeftId].Height, nodes[yNode.RightId].Height) + 1;
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LeftRightRotate(TreeNode[] nodes, int a)
        {
            ref var aNode = ref nodes[a];
            int b = aNode.LeftId;
            ref var bNode = ref nodes[b];
            int c = bNode.RightId;
            ref var cNode = ref nodes[c];
            bNode.RightId = cNode.LeftId;
            aNode.LeftId = cNode.RightId;
            cNode.LeftId = b;
            cNode.RightId = a;
            bNode.Height = Math.Max(nodes[bNode.LeftId].Height, nodes[bNode.RightId].Height) + 1;
            aNode.Height = Math.Max(nodes[aNode.LeftId].Height, nodes[aNode.RightId].Height) + 1;
            cNode.Height = Math.Max(nodes[cNode.LeftId].Height, nodes[cNode.RightId].Height) + 1;
            return c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RightLeftRotate(TreeNode[] nodes, int a)
        {
            ref var aNode = ref nodes[a];
            int b = aNode.RightId;
            ref var bNode = ref nodes[b];
            int c = bNode.LeftId;
            ref var cNode = ref nodes[c];
            aNode.RightId = cNode.LeftId;
            bNode.LeftId = cNode.RightId;
            cNode.LeftId = a;
            cNode.RightId = b;
            bNode.Height = Math.Max(nodes[bNode.LeftId].Height, nodes[bNode.RightId].Height) + 1;
            aNode.Height = Math.Max(nodes[aNode.LeftId].Height, nodes[aNode.RightId].Height) + 1;
            cNode.Height = Math.Max(nodes[cNode.LeftId].Height, nodes[cNode.RightId].Height) + 1;
            return c;
        }

        internal virtual void Add(T data)
        {
            if (_nodesCount + 1 == int.MaxValue)
                throw new Exception("collection overflow");

            var nodes = _nodes;
            var nodesStack = _nodesStack;
            int nodeIdx = _root;
            int stackCount = 0;

            while (nodeIdx != EmptyNode)
            {
                ref var node = ref nodes[nodeIdx];
                int cmp = data.CompareTo(node.Data);
                if (cmp < 0)
                {
                    //last bit - left or right
                    nodesStack[stackCount++] = nodeIdx | int.MinValue;
                    nodeIdx = node.LeftId;
                }
                else if (cmp > 0)
                {
                    nodesStack[stackCount++] = nodeIdx & int.MaxValue;
                    nodeIdx = node.RightId;
                }
                else
                {
                    node.Data = data;
                    return;
                }
            }

            _isDirty = true;
            _nodesCount++;
            if (_nodesCount >= nodes.Length)
            {
                Array.Resize(ref _nodes, _nodesCount * 2);
                nodes = _nodes;
            }


            //get new node id
            nodeIdx = _idStackCount > 0 ? nodes[--_idStackCount].IdStack : _idCounter++;
            nodes[nodeIdx].SetData(ref data);

            while (stackCount > 0)
            {
                int nodePair = nodesStack[--stackCount];
                int newNodeIdx = nodePair & int.MaxValue;
                ref var node = ref nodes[newNodeIdx];
                if ((nodePair & int.MinValue) != 0) //last bit is left
                    node.LeftId = nodeIdx;
                else
                    node.RightId = nodeIdx;

                ref var leftChild = ref nodes[node.LeftId];
                ref var rightChild = ref nodes[node.RightId];
                switch (leftChild.Height - rightChild.Height) //balance
                {
                    case > 1:
                        nodeIdx = data.CompareTo(leftChild.Data!) < 0
                            ? RightRotate(nodes, newNodeIdx)
                            : LeftRightRotate(nodes, newNodeIdx);
                        break;
                    case < -1:
                        nodeIdx = data.CompareTo(rightChild.Data!) > 0
                            ? LeftRotate(nodes, newNodeIdx)
                            : RightLeftRotate(nodes, newNodeIdx);
                        break;
                    default:
                    {
                        int height = (leftChild.Height > rightChild.Height ? leftChild.Height : rightChild.Height) + 1;
                        if (height == node.Height)
                            return;
                        nodeIdx = newNodeIdx;
                        //update new height
                        node.Height = height;
                        break;
                    }
                }
            }

            _root = nodeIdx;
        }

        public bool Contains(T item)
        {
            int nodeIdx = _root;
            var nodes = _nodes;
            while (nodeIdx != EmptyNode)
            {
                int cmp = item.CompareTo(nodes[nodeIdx].Data);
                if (cmp == 0)
                    return true;
                nodeIdx = cmp < 0 ? nodes[nodeIdx].LeftId : nodes[nodeIdx].RightId;
            }

            return false;
        }
        
        public bool TryGetMin(out T element)
        {
            int nodeIdx = _root;
            var nodes = _nodes;
            while (nodeIdx != EmptyNode)
            {
                int minId = nodes[nodeIdx].LeftId;
                if (minId == EmptyNode)
                {
                    element = nodes[nodeIdx].Data;
                    return true;
                }
                nodeIdx = minId;
            }
            element = default;
            return false;
        }

        public bool TryGetMax(out T element)
        {
            int nodeIdx = _root;
            var nodes = _nodes;
            while (nodeIdx != EmptyNode)
            {
                int maxId = nodes[nodeIdx].RightId;
                if (maxId == EmptyNode)
                {
                    element = nodes[nodeIdx].Data;
                    return true;
                }
                nodeIdx = maxId;
            }
            element = default;
            return false;
        }

        internal virtual bool Remove(T data)
        {
            int nodesCountBeforeRemove = _nodesCount;
            _root = DeleteNode(_root, data);
            return nodesCountBeforeRemove == _nodesCount + 1;
        }

        private int DeleteNode(int nodeIdx, T data)
        {
            if (nodeIdx == EmptyNode)
                return EmptyNode;

            var nodes = _nodes;
            ref var node = ref nodes[nodeIdx];
            int cmp = data.CompareTo(node.Data);
            if (cmp < 0)
                node.LeftId = DeleteNode(node.LeftId, data);
            else if (cmp > 0)
                node.RightId = DeleteNode(node.RightId, data);
            else
            {
                if (node.LeftId == EmptyNode || node.RightId == EmptyNode)
                {
                    //reuse id later
                    nodes[_idStackCount++].IdStack = nodeIdx;
                    _nodesCount--;
                    _isDirty = true;

                    nodeIdx = node.LeftId != EmptyNode ? node.LeftId : node.RightId;
                    if (nodeIdx == EmptyNode)
                        return EmptyNode;

                    node = ref nodes[nodeIdx];
                }
                else
                {
                    var temp = nodes[node.RightId];
                    while (temp.LeftId != EmptyNode)
                        temp = nodes[temp.LeftId];
                    node.Data = temp.Data;
                    node.RightId = DeleteNode(node.RightId, node.Data);
                }
            }

            int balance = UpdateHeightAndGetBalance(nodes, ref node);
            if (balance > 1)
                return GetNodeBalance(nodes, node.LeftId) >= 0
                    ? RightRotate(nodes, nodeIdx)
                    : LeftRightRotate(nodes, nodeIdx);
            if (balance < -1)
                return GetNodeBalance(nodes, node.RightId) <= 0
                    ? LeftRotate(nodes, nodeIdx)
                    : RightLeftRotate(nodes, nodeIdx);

            return nodeIdx;
        }

        private void CacheInorder()
        {
            _isDirty = false;
            int stackCount = 0;
            int curr = _root;
            int index = 0;
            var nodesStack = _nodesStack;
            var nodes = _nodes;

            while (curr != EmptyNode || stackCount > 0)
            {
                while (curr != EmptyNode)
                {
                    nodesStack[stackCount++] = curr;
                    curr = nodes[curr].LeftId;
                }

                curr = nodesStack[--stackCount];
                //cache ordered
                nodes[index++].SortedNextId = curr;
                curr = nodes[curr].RightId;
            }
        }

        internal virtual void Clear()
        {
            _root = EmptyNode;
            _idCounter = 1;
            _isDirty = false;
            _nodesCount = 0;
            _idStackCount = 0;
            _enumeratorsCount = 0;
        }

        public Enumerator GetEnumerator()
        {
            if (_isDirty && _enumeratorsCount == 0)
                CacheInorder();
            return new Enumerator(this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}