namespace LiteEntitySystem.Tests
{
    public enum TestEntityId : byte
    {
        TestEntity,
        Player
    }

    public sealed class TestEntity : EntityLogic
    {
        private SyncVar<int> _testSyncValue;
        public TestEntity(EntityParams entityParams) : base(entityParams) {}
        public void SetValue() => _testSyncValue.Value = 351122;
        public bool CheckValue() => _testSyncValue == 351122;
    }

    public class SharedPlayer : EntityLogic
    {
        private SyncVar<int> _health;
        public SharedPlayer(EntityParams entityParams) : base(entityParams) {}
        public void SetHealth(int health) => _health.Value = health;
    }

    public sealed class ServerPlayer : SharedPlayer
    {
        public ServerPlayer(EntityParams entityParams) : base(entityParams) {}
    }

    public sealed class ClientPlayer : SharedPlayer
    {
        public ClientPlayer(EntityParams entityParams) : base(entityParams) {}
    }
}