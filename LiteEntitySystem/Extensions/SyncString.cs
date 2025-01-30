using System;
using System.Text;

namespace LiteEntitySystem.Extensions
{
    /// <summary>
    /// A SyncableField that holds a string. On the server side, setting Value
    /// replicates the new string data to clients. On the client side, when
    /// updated from the server, it fires OnValueChanged(string).
    /// </summary>
    public class SyncString : SyncableField<string>
    {
        private static readonly UTF8Encoding Encoding = new(false, true);
        private byte[] _stringData;
        private string _string;
        private int _size;

        private static RemoteCallSpan<byte> _setStringClientCall;

        public override event EventHandler<SyncVarChangedEventArgs<string>> ValueChanged;

        /// <summary>
        /// The user-facing property. If we are on the server and set it,
        /// we replicate that new string to all clients.
        /// </summary>
        public override string Value
        {
            get => _string;
            set
            {
                if (_string == value)
                    return;
                _string = value;
                Utils.ResizeOrCreate(ref _stringData, Encoding.GetMaxByteCount(_string.Length));
                _size = Encoding.GetBytes(_string, 0, _string.Length, _stringData, 0);
                ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
            }
        }

        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetNewString, ref _setStringClientCall);
        }

        public override string ToString()
        {
            return _string;
        }

        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }
        
        private void SetNewString(ReadOnlySpan<byte> data)
        {
            string newVal = Encoding.GetString(data);
            if (_string != newVal)
            {
                ValueChanged?.Invoke(this, new SyncVarChangedEventArgs<string>(_string, newVal));
                _string = newVal;
            }
           
        }

        protected internal override void OnSyncRequested()
        {
            ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
        }
    }
}