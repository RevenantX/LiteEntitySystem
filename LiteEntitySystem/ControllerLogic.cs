using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for Controller entities
    /// </summary>
    public abstract partial class ControllerLogic : InternalEntity
    {
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        internal SyncVar<byte> InternalOwnerId;
        
        [SyncVarFlags(SyncFlags.NeverRollBack)]
        private SyncVar<EntitySharedReference> _controlledEntity;

        public byte OwnerId => InternalOwnerId;
        
        /// <summary>
        /// Is controller - AI controller
        /// </summary>
        public abstract bool IsBot { get; }

        public T GetControlledEntity<T>() where T : PawnLogic
        {
            return EntityManager.GetEntityById<T>(_controlledEntity);
        }

        internal override bool IsControlledBy(byte playerId)
        {
            return InternalOwnerId == playerId;
        }
        
        public virtual void BeforeControlledUpdate()
        {
            
        }

        public void DestroyWithControlledEntity()
        {
            GetControlledEntity<PawnLogic>()?.Destroy();
            _controlledEntity.Value = null;
            Destroy();
        }

        public void StartControl(PawnLogic target)
        {
            StopControl();
            _controlledEntity.Value = target;
            GetControlledEntity<PawnLogic>().Controller = this;
        }

        internal void OnControlledDestroy()
        {
            StopControl();
        }

        public void StopControl()
        {
            var controlledLogic = GetControlledEntity<PawnLogic>();
            if (controlledLogic == null)
                return;
            controlledLogic.Controller = null;
            _controlledEntity.Value = null;
        }
        
        protected ControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for AI Controller entities
    /// </summary>
    [LocalOnly, UpdateableEntity]
    public abstract partial class AiControllerLogic : ControllerLogic
    {
        public override bool IsBot => true;
        
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }

    /// <summary>
    /// Base class for AI Controller entities with typed ControlledEntity field
    /// </summary>
    [LocalOnly, UpdateableEntity]
    public abstract partial class AiControllerLogic<T> : AiControllerLogic where T : PawnLogic
    {
        public T ControlledEntity => GetControlledEntity<T>();
        
        protected AiControllerLogic(EntityParams entityParams) : base(entityParams) { }
    }
}