using LiteEntitySystem;
using LiteEntitySystem.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteEntitySystem.Tests;

[TestClass]
public class DirectConnectionTests
{
    private static byte TestLESHeader = 0x42;
    private static byte TestFPS = 60;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ServerPeerCanRouteReceivedDataToItsPlayer()
    {
        Logger.LoggerImpl = new TestLogger(TestContext);
        var connection = new DirectConnection();
        var typesMap = new EntityTypesMap<TestEntityId>()
            .Register(TestEntityId.TestEntity, entityParams => new TestEntity(entityParams));
        var server = new ServerEntityManager(typesMap, TestLESHeader, TestFPS, ServerSendRate.EqualToFPS);
        server.AddEntity<TestEntity>().SetValue();
        
        var player = server.AddPlayer(connection.ServerPeer);
        DeserializeResult result = DeserializeResult.Done;
        connection.ServerPeer.PacketReceived += packet => result = server.Deserialize(connection.ServerPeer, packet.Span);
        
        var client = new ClientEntityManager(typesMap, connection.ClientPeer, TestLESHeader);
        connection.ClientPeer.PacketReceived += packet => result = client.Deserialize(packet.Span);

        var startTime = DateTime.UtcNow;
        bool entitySynchronized = false;
        while((DateTime.UtcNow - startTime).TotalSeconds < 3)
        {
            server.Update();
            connection.Pump();
            client.Update();
            Thread.Sleep(1);
            foreach(var e in client.GetEntities<TestEntity>())
            {
                if(e.IsConstructed)
                {
                    entitySynchronized = e.CheckValue();
                    break;
                }
            }
        }

        Assert.IsTrue(entitySynchronized);
        Assert.AreSame(connection.ServerPeer, player.Peer);
        Assert.AreEqual(DeserializeResult.Done, result);
    }

    [TestMethod]
    public void ClassDataHashIgnoresLogicOnlySubclassIdentity()
    {
        var serverTypes = new EntityTypesMap<TestEntityId>()
            .Register(TestEntityId.Player, entityParams => new ServerPlayer(entityParams));
        var clientTypes = new EntityTypesMap<TestEntityId>()
            .Register(TestEntityId.Player, entityParams => new ClientPlayer(entityParams));
        var connection = new DirectConnection();
        var server = new ServerEntityManager(serverTypes, TestLESHeader, TestFPS, ServerSendRate.EqualToFPS);
        var client = new ClientEntityManager(clientTypes, connection.ClientPeer, TestLESHeader);

        Assert.AreEqual(server.ClassDataHash, client.ClassDataHash);
    }

    [TestCleanup]
    public void ClearLogger()
    {
        Logger.LoggerImpl = null;
    }

    private sealed class TestLogger : ILogger
    {
        private readonly TestContext _testContext;
        public TestLogger(TestContext testContext) => _testContext = testContext;
        public void Log(string log) => _testContext.WriteLine($"[INFO] {log}");
        public void LogError(string log) => _testContext.WriteLine($"[ERROR] {log}");
        public void LogWarning(string log) => _testContext.WriteLine($"[WARNING] {log}");
    }
}
