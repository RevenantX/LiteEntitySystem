// See https://aka.ms/new-console-template for more information
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;

struct MyInput
{
    public int A;
    public int B;
}

enum MyIds
{
    BasePlayer,
    BaseController
}

partial class BasePlayer : PawnLogic
{
    public SyncVar<int> TestSyncVar;
    public readonly SyncString SyncStr = new SyncString();
    
    public BasePlayer(EntityParams entityParams) : base(entityParams)
    {

    }
}

partial class BasePlayerController : HumanControllerLogic<MyInput>
{
    public BasePlayerController(EntityParams entityParams) : base(entityParams)
    {
    }

    public override void ReadInput(in MyInput input)
    {
        
    }

    public override void GenerateInput(out MyInput input)
    {
        input = new MyInput();
    }
}

class TestPeer : AbstractNetPeer
{
    public ClientEntityManager ClientTarget;
    public ServerEntityManager ServerTarget;
    public TestPeer ServerPeer;
    
    public override void TriggerSend() { }

    public override void SendReliableOrdered(ReadOnlySpan<byte> data)
    {
        ClientTarget?.Deserialize(data);
        ServerTarget?.Deserialize(ServerPeer, data);
    }

    public override void SendUnreliable(ReadOnlySpan<byte> data)
    {
        ClientTarget?.Deserialize(data);
        ServerTarget?.Deserialize(ServerPeer, data);
    }

    public override int GetMaxUnreliablePacketSize() => 1024;
}

class TestLogger : ILogger
{
    public void Log(string log)
    {
        Console.WriteLine(log);
    }

    public void LogError(string log)
    {
        Console.WriteLine(log);
    }

    public void LogWarning(string log)
    {
        Console.WriteLine(log);
    }
}

class Program
{
    public static void Main(string[] args)
    {
        Logger.LoggerImpl = new TestLogger();
        var typesMap = new EntityTypesMap<MyIds>()
            .Register(MyIds.BasePlayer, e => new BasePlayer(e))
            .Register(MyIds.BaseController, e => new BasePlayerController(e));

        var clientPeer = new TestPeer();
        var serverPeer = new TestPeer();
        var cem = new ClientEntityManager(typesMap, new InputProcessor<MyInput>(), clientPeer, 0, 30);
        var sem = new ServerEntityManager(typesMap, new InputProcessor<MyInput>(), 0, 30, ServerSendRate.EqualToFPS);
        clientPeer.ServerTarget = sem;
        clientPeer.ServerPeer = serverPeer;
        serverPeer.ClientTarget = cem;
        var player = sem.AddPlayer(serverPeer);
        var playerEntity = sem.AddEntity<BasePlayer>();
        var playerController = sem.AddController<BasePlayerController>(player, e => e.StartControl(playerEntity));
        for (int i = 0; i < 100; i++)
        {
            sem.Update();
            cem.Update();
            Thread.Sleep(1);
        }
    }
}