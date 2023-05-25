using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for singletons entity that can exists in only one instance
    /// </summary>
    public abstract class SingletonEntityLogic : InternalEntity
    {
        internal override bool IsControlledBy(byte playerId)
        {
            return false;
        }

        protected SingletonEntityLogic(EntityParams entityParams) : base(entityParams) { }
    }
}