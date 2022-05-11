using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace LiteEntitySystem
{
    [AttributeUsage(AttributeTargets.Method)]
    public class SyncableRemoteCall : Attribute
    {
        internal byte Id = byte.MaxValue;
        internal int DataSize;
        internal MethodCallDelegate MethodDelegate;
    }
    
    public abstract class SyncableField
    {
        //This setups in Serializer.Init
        internal ServerEntityManager EntityManager;
        internal byte FieldId;
        internal ushort EntityId;
        
        public abstract unsafe void FullSyncWrite(byte* data, ref int position);
        public abstract unsafe void FullSyncRead(byte* data, ref int position);
        
        protected void ExecuteOnClient<T>(Action<T> methodToCall, T value) where T : struct
        {
            if (EntityManager == null)
                return;
            
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            EntityManager.AddSyncableCall(this, value, methodToCall.Method);
        }
        
        protected void ExecuteOnClient<T>(Action<T[]> methodToCall, T[] value, int count) where T : struct
        {
            if (EntityManager == null)
                return;
            
            if (methodToCall.Target != this)
                throw new Exception("You can call this only on this class methods");
            EntityManager.AddSyncableCall(this, value, count, methodToCall.Method);
        }
    }
    
    public class SyncString : SyncableField
    {
        private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);
        private byte[] _stringData;
        private string _string;
        private int _size;
        
        public string Value
        {
            get => _string;
            set
            {
                if (_string == value)
                    return;
                _string = value;
                Utils.ResizeOrCreate(ref _stringData, Encoding.GetMaxByteCount(_string.Length));
                _size = Encoding.GetBytes(_string, 0, _string.Length, _stringData, 0);
                ExecuteOnClient(SetNewString, _stringData, _size);
            }
        }

        public override string ToString()
        {
            return _string;
        }

        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }

        [SyncableRemoteCall]
        private void SetNewString(byte[] data)
        {
            _string = Encoding.GetString(data, 0, data.Length);
        }

        public override unsafe void FullSyncRead(byte* data, ref int position)
        {
            int length = *(ushort*)(data + position);
            Utils.ResizeOrCreate(ref _stringData, length);
            _string = Encoding.GetString(data + position + sizeof(ushort), length);
            position += sizeof(ushort) + length;
        }

        public override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            *(ushort*)(data + position) = (ushort)_size;
            fixed (byte* stringData = _stringData)
            {
                Unsafe.CopyBlock(data + position + sizeof(ushort), stringData, (uint)_size);
            }
            position += sizeof(ushort) + _size;
        }
    }
}