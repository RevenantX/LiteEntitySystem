using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    public abstract class SyncableField : InternalBaseClass
    {
        internal InternalEntity ParentEntityInternal;
        internal ExecuteFlags Flags;
        internal ushort RPCOffset;
        
        /// <summary>
        /// Is syncableField on client
        /// </summary>
        protected internal bool IsClient => !IsServer;
        
        /// <summary>
        /// Is syncableField on server
        /// </summary>
        protected internal bool IsServer => ParentEntityInternal != null && ParentEntityInternal.IsServer;

        protected internal virtual void BeforeReadRPC()
        {
            
        }

        protected internal virtual void AfterReadRPC()
        {
            
        }

        protected internal virtual void OnRollback()
        {
            
        }

        protected internal virtual void RegisterRPC(ref SyncableRPCRegistrator r)
        {

        }
    }
}