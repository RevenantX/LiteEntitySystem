namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for entites that can be controlled by Controller
    /// </summary>
    [UpdateableEntity]
    public abstract class PawnLogic : EntityLogic
    {
        private SyncEntityReference _controller;

        public ControllerLogic Controller
        {
            get => EntityManager.GetEntityById<ControllerLogic>(_controller);
            internal set
            {
                byte ownerId = EntityManager.ServerPlayerId;
                if (value != null)
                {
                    var parent = GetParent<EntityLogic>();
                    if (parent != null)
                    {
                        ownerId = parent.OwnerId;
                    }
                }
                SetOwner(this, ownerId);
                _controller = value;
            }
        }

        public override void Update()
        {
            base.Update();
            Controller?.BeforeControlledUpdate();
        }

        internal override void DestroyInternal()
        {
            if (EntityManager.IsServer)
            {
                Controller?.OnControlledDestroy();
            }
            base.DestroyInternal();
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams) { }
    }
}