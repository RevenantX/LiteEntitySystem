using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem
{
    public class SyncList<T> : SyncableField, IList<T> where T : struct
    {
        private T[] _data;
        private int _count;
        public int Count => _count;
        public bool IsReadOnly => false;

        public SyncList()
        {
            _data = new T[8];
        }

        public SyncList(int capacity)
        {
            _data = new T[capacity];
        }

        public T[] ToArray()
        {
            var arr = new T[_count];
            Buffer.BlockCopy(_data, 0, arr, 0, _count);
            return arr;
        }

        [SyncableRemoteCall]
        public void Add(T item)
        {
            if (_data.Length == _count)
                Array.Resize(ref _data, Math.Min(_data.Length * 2, ushort.MaxValue));
            _data[_count] = item;
            _count++;
            ExecuteOnClient(Add, item);
        }

        [SyncableRemoteCall]
        public void Clear()
        {
            _count = 0;
            ExecuteOnClient(Clear);
        }

        [SyncableRemoteCall]
        public void FullClear()
        {
            if (_count == 0)
                return;
            Array.Clear(_data, 0, _count);
            _count = 0;
            ExecuteOnClient(FullClear);
        }

        public bool Contains(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                    return true;
            }
            return false;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(_data, 0, array, arrayIndex, _count);
        }
        
        public bool Remove(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                {
                    RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public int IndexOf(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_data[i].Equals(item))
                    return i;
            }
            return -1;
        }

        public void Insert(int index, T item)
        {
            throw new NotImplementedException();
        }

        [SyncableRemoteCall]
        public void RemoveAt(int index)
        {
            _data[index] = _data[_count - 1];
            _data[_count - 1] = default;
            _count--;
            ExecuteOnClient(RemoveAt, index);
        }

        T IList<T>.this[int index]
        {
            get => _data[index];
            set => _data[index] = value;
        }

        public ref T this[int index] => ref _data[index];
        
        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            Unsafe.Write(data + position, (ushort)_count);
            
            byte[] byteData = Unsafe.As<byte[]>(_data);
            int bytesCount = Unsafe.SizeOf<T>() * _count;
            
            fixed(void* rawData = byteData)
                Unsafe.CopyBlock(data + position + sizeof(ushort), rawData, (uint)bytesCount);
            
            position += sizeof(ushort) + bytesCount;
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            _count = Unsafe.Read<ushort>(data + position);
            
            if (_data.Length < _count)
                Array.Resize(ref _data, Math.Max(_data.Length * 2, _count));
            
            byte[] byteData = Unsafe.As<byte[]>(_data);
            int bytesCount = Unsafe.SizeOf<T>() * _count;
            
            fixed(void* rawData = byteData)
                Unsafe.CopyBlock(rawData, data + position + sizeof(ushort), (uint)bytesCount);
            
            position += sizeof(ushort) + bytesCount;
        }

        public IEnumerator<T> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}