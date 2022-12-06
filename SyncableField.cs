using System;

namespace LiteEntitySystem
{
    public abstract class SyncableField
    {
        internal byte FieldId;

        public virtual void FullSyncWrite(Span<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void FullSyncRead(ReadOnlySpan<byte> dataSpan, ref int position)
        {
            
        }

        public virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {

        }
    }
}