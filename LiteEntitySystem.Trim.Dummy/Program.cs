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
    BaseController,
    BasePlayerTest
}

partial class SyncableTest : SyncableField
{
    public SyncVar<int> IntVar;
    public SyncVar<float> FloatVar;
}

partial class SyncableTestDerived : SyncableTest
{
    public SyncVar<int> IntVar2;
    public SyncVar<float> FloatVar2;
}

[UpdateableEntity(true)]
partial class BasePlayer : PawnLogic
{
    private static RemoteCall RpcTest2;
    public readonly SyncList<int> SyncLst = new SyncList<int>();
    public SyncVar<int> TestSyncVar;
    private SyncVar<float> TestSyncVar2;
    private readonly SyncString SyncStr = new SyncString();
    public readonly SyncString SyncStr2 = new SyncString();
    public SyncVar<long> TestSyncVar3;
    private static RemoteCall RpcTest;

    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> FlagsTest1;
    
    [SyncVarFlags(SyncFlags.LagCompensated)]
    public SyncVar<float> FlagsTest2;
    
    [SyncVarFlags(SyncFlags.LagCompensated | SyncFlags.Interpolated)]
    public SyncVar<float> FlagsTest3;
    
    public BasePlayer(EntityParams entityParams) : base(entityParams)
    {

    }

    protected override void OnConstructed()
    {
        base.OnConstructed();
        if (EntityManager.IsServer)
        {
            SyncStr.Value = "Ass";
            SyncStr2.Value = "1234567";
        }
    }

    protected override void Update()
    {
        base.Update();
        if (EntityManager.IsServer)
        {
            ExecuteRPC(RpcTest);
        }
    }

    private void RpcMethod()
    {
        Console.WriteLine($"GOT {SyncStr.Value} {SyncStr2.Value}");
    }

    protected override void RegisterRPC(in RPCRegistrator r)
    {
        base.RegisterRPC(in r);
        Console.WriteLine($"RegisterRPC {GetType().Name}");
        r.CreateRPCAction(this, RpcMethod, ref RpcTest, ExecuteFlags.SendToAll);
    }
}

partial class BasePlayerTest : BasePlayer
{
    private static RemoteCall BPTest1;
    public readonly SyncList<int> BPTest2 = new SyncList<int>();
    public SyncVar<int> BPTest3;
    public SyncVar<float> BPTest4;
    public readonly SyncString BPTest5 = new SyncString();
    public readonly SyncString BPTest6 = new SyncString();
    public SyncVar<long> BPTest7;
    private static RemoteCall BPTest8;

    public BasePlayerTest(EntityParams entityParams) : base(entityParams)
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
        //if(ClientTarget != null)
        //    Console.WriteLine($"SendToClient REL: {data.Length}");
        ClientTarget?.Deserialize(data);
        ServerTarget?.Deserialize(ServerPeer, data);
    }

    public override void SendUnreliable(ReadOnlySpan<byte> data)
    {
        //if(ClientTarget != null)
        //    Console.WriteLine($"SendToClient UNREL: {data.Length}");
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
            .Register(MyIds.BaseController, e => new BasePlayerController(e))
            .Register(MyIds.BasePlayerTest, e => new BasePlayerTest(e));

        var clientPeer = new TestPeer();
        var serverPeer = new TestPeer();
        var cem = new ClientEntityManager(typesMap, new InputProcessor<MyInput>(), clientPeer, 0, 30);
        var sem = new ServerEntityManager(typesMap, new InputProcessor<MyInput>(), 0, 30, ServerSendRate.EqualToFPS);
        clientPeer.ServerTarget = sem;
        clientPeer.ServerPeer = serverPeer;
        serverPeer.ClientTarget = cem;
        var player = sem.AddPlayer(serverPeer);
        var playerEntity = sem.AddEntity<BasePlayer>();
        var testPlayerEntity = sem.AddEntity<BasePlayerTest>();
        var playerController = sem.AddController<BasePlayerController>(player, e => e.StartControl(playerEntity));
        
        for (int i = 0; i < 1000; i++)
        {
            sem.Update();
            cem.Update();
            Thread.Sleep(1);
        }
    }
}