namespace LiteEntitySystem
{
    /// <summary>
    /// Base class for entites that can be controlled by Controller
    /// </summary>
    [UpdateableEntity]
    public abstract class PawnLogic : EntityLogic
    {
        [SyncVar] 
        private EntitySharedReference _controller;

        public ControllerLogic Controller
        {
            get => EntityManager.GetEntityById<ControllerLogic>(_controller);
            internal set
            {
                SetOwner(this, value?.InternalOwnerId ?? (GetParent<EntityLogic>()?.InternalOwnerId ?? ServerEntityManager.ServerPlayerId));
                _controller = value;
            }
        }

        public override void Update()
        {
            base.Update();
            Controller?.BeforeControlledUpdate();
        }

        protected override void OnDestroy()
        {
            Controller?.OnControlledDestroy();
        }

        protected PawnLogic(EntityParams entityParams) : base(entityParams) { }
    }
}