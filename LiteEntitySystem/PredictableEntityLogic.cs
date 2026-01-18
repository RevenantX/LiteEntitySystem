namespace LiteEntitySystem
{
    /// <summary>
    /// Entity that can be spawned on client prediction
    /// </summary>
    public abstract class PredictableEntityLogic : EntityLogic
    {
        private struct InitialData
        {
            public ushort PredictedId;
            public EntitySharedReference Parent;
        }
        
        private ushort _predictedId;
        private EntitySharedReference _initialParent;
        private static RemoteCall<InitialData> SyncRPC;
        
        //used for spawn prediction
        internal readonly ushort CreatedAtTick;

        /// <summary>
        /// Is entity is recrated from server when created using AddPredictedEntity
        /// Can be true only on client
        /// </summary>
        public bool IsRecreated { get; internal set; }

        internal void InitEntity(ushort predictedId, EntitySharedReference initialParent)
        {
            //Logger.Log($"InitEntity. PredId: {predictedId}. Id: {Id}, Class: {ClassData.ClassEnumName}. Mode: {EntityManager.Mode}. InitalParrent: {initialParent}");
            _predictedId = predictedId;
            _initialParent = initialParent;
        }
        
        protected PredictableEntityLogic(EntityParams entityParams) : base(entityParams)
        {
            CreatedAtTick = entityParams.EntityManager.Tick;
        }
        
        internal bool IsSameAsLocal(PredictableEntityLogic other) =>
            _predictedId == other._predictedId && _initialParent == other._initialParent && ClassId == other.ClassId;

        //used for finding entity in rollback
        internal bool IsEntityMatch(ushort predictedId, ushort parentId, ushort createdAtTick) => 
            _predictedId == predictedId && _initialParent.Id == parentId && CreatedAtTick == createdAtTick;

        protected override void RegisterRPC(ref RPCRegistrator r)
        {
            base.RegisterRPC(ref r);
            r.CreateRPCAction(this, OnSyncRPC, ref SyncRPC, ExecuteFlags.SendToOwner);
        }

        private void OnSyncRPC(InitialData initialData)
        {
            _predictedId = initialData.PredictedId;
            _initialParent = initialData.Parent;
            //Logger.Log($"OnSyncRPC called. pred id: {_predictedId}, initial parent: {_initialParent}");
        }

        protected internal override void OnSyncRequested()
        {
            base.OnSyncRequested();
            ExecuteRPC(SyncRPC, new InitialData { PredictedId = _predictedId, Parent = ParentId });
        }
    }
}