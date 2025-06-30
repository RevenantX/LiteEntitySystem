namespace LiteEntitySystem.Internal
{
    /// <summary>
    /// Base class for SyncableFields and Entities
    /// </summary>
    public abstract class InternalBaseClass
    {
        /// <summary>
        /// Method for executing RPCs containing initial sync data that need to be sent after entity creation
        /// to existing players or when new player connected
        /// </summary>
        protected internal virtual void OnSyncRequested()
        {
            
        }
    }
}