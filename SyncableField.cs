using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal bool IsDirty;
        internal ushort ChangedTick;

        internal virtual void Write(ushort tick)
        {
            ChangedTick = tick;
        }
        
        internal unsafe abstract void PartialSyncWrite(StateSerializer serializer);
        internal unsafe abstract void FullSyncWrite(byte* data, ref int position);
        internal unsafe abstract void FullSyncRead(byte* data, ref int position);
    }
    
    public class SyncString : SyncableField
    {
        private readonly NetDataWriter _stringData = new NetDataWriter();
        private string _string;
        public string Value
        {
            get => _string;
            set
            {
                if (_string == value)
                    return;
                IsDirty = true;
                _string = value;
            }
        }
        
        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }

        internal override void Write(ushort tick)
        {
            base.Write(tick);
            _stringData.Reset();
            _stringData.Put(_string);
        }

        internal override unsafe void PartialSyncWrite(StateSerializer serializer)
        {
            
        }

        internal override unsafe void FullSyncRead(byte* data, ref int position)
        {
            
        }

        internal override unsafe void FullSyncWrite(byte* data, ref int position)
        {
            
        }
    }
}