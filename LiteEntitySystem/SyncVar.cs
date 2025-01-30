using System;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    
    [Flags]
    public enum SyncFlags : byte
    {
        None                = 0,
        Interpolated        = 1,
        LagCompensated      = 1 << 1,
        OnlyForOtherPlayers = 1 << 2,
        OnlyForOwner        = 1 << 3,
        AlwaysRollback      = 1 << 4,
        NeverRollBack       = 1 << 5
    }
    
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class)]
    public class SyncVarFlags : Attribute
    {
        public readonly SyncFlags Flags;
        public readonly OnSyncExecutionOrder OnSyncExecutionOrder;
        
        public SyncVarFlags(SyncFlags flags)
        {
            Flags = flags;
        }
        
        public SyncVarFlags(OnSyncExecutionOrder executionOrder)
        {
            OnSyncExecutionOrder = executionOrder;
        }
        
        public SyncVarFlags(SyncFlags flags, OnSyncExecutionOrder executionOrder)
        {
            Flags = flags;
            OnSyncExecutionOrder = executionOrder;
        }
    }
    
    public class SyncVarChangedEventArgs<T> : EventArgs
    {
        public T OldValue { get; }
        public T NewValue { get; }

        public SyncVarChangedEventArgs(T oldValue, T newValue)
        {
            OldValue = oldValue;
            NewValue = newValue;
        }
    }
    
    public interface INotifySyncVarChanged<T>
    {
        event EventHandler<SyncVarChangedEventArgs<T>> ValueChanged;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncVar<T> : INotifySyncVarChanged<T>, IEquatable<T>, IEquatable<SyncVar<T>> where T : unmanaged
    {
        private T _value;
        internal ushort FieldId;
        internal InternalEntity Container;
        
        internal void SetDirect(T value) => _value = value;
        
        public event EventHandler<SyncVarChangedEventArgs<T>> ValueChanged;
        
        public T Value
        {
            get => _value;
            set
            {
                if (!Utils.FastEquals(ref value, ref _value))
                {
                    Container?.EntityManager.EntityFieldChanged(Container, FieldId, ref value);
                    ValueChanged?.Invoke(this, new SyncVarChangedEventArgs<T>(_value, value));
                }
                _value = value;
            }
        }
        

        internal void Init(InternalEntity container, ushort fieldId)
        {
            Container = container;
            FieldId = fieldId;
            Container?.EntityManager.EntityFieldChanged(Container, FieldId, ref _value);
        }
        
        internal unsafe bool SetFromAndSync(byte* data)
        {
            if (!Utils.FastEquals(ref _value, data))
            {
                var temp = _value;
                _value = *(T*)data;
                *(T*)data = temp;
                return true;
            }
            _value = *(T*)data;
            return false;
        }
        
        public static implicit operator T(SyncVar<T> sv) => sv._value;

        public override string ToString() => _value.ToString();

        public override int GetHashCode() => _value.GetHashCode();

        public override bool Equals(object o) => o is SyncVar<T> sv && Utils.FastEquals(ref sv._value, ref _value);
        
        public static bool operator==(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a._value, ref b._value);

        public static bool operator!=(SyncVar<T> a, SyncVar<T> b) => Utils.FastEquals(ref a._value, ref b._value) == false;
        
        public static bool operator==(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b._value);
        
        public static bool operator!=(T a, SyncVar<T> b) => Utils.FastEquals(ref a, ref b._value) == false;
        
        public static bool operator==(SyncVar<T> a, T b) => Utils.FastEquals(ref b, ref a._value);
        
        public static bool operator!=(SyncVar<T> a, T b) => Utils.FastEquals(ref b, ref a._value) == false;

        public bool Equals(T v) => Utils.FastEquals(ref _value, ref v);
        
        public bool Equals(SyncVar<T> tv) => Utils.FastEquals(ref _value, ref tv._value);
        
    }
}