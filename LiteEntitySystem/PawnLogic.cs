namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for entites that can be controlled by Controller
    /// </summary>
    [EntityFlags(EntityFlags.Updateable)]
    public abstract class PawnLogic : EntityLogic
    {
        [SyncVarFlags(SyncFlags.OnlyForOwner)]
        private SyncVar<EntitySharedReference> _controller;

        public ControllerLogic Controller
        {
            get => EntityManager.GetEntityById<ControllerLogic>(_controller);
            internal set
            {
                byte ownerId = EntityManager.ServerPlayerId;
                if (value != null)
                {
                    var parent = GetParent<EntityLogic>();
                    ownerId = parent != null ? parent.InternalOwnerId : value.InternalOwnerId;
                }
                SetOwner(this, ownerId);
                _controller.Value = new EntitySharedReference(value);
            }
        }

        protected internal override void Update()
        {
            base.Update();
            Controller?.BeforeControlledUpdate();
        }

        internal override void DestroyInternal()
        {
            if (EntityManager.IsServer)
                Controller?.StopControl();
            base.DestroyInternal();
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams) { }
    }
}