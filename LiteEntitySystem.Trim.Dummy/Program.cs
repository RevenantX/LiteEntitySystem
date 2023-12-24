// See https://aka.ms/new-console-template for more information
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
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
    [BindRpc(nameof(RPCExec))]
    private static RemoteCall _testRpc;

    private void RPCExec()
    {
        //Console.WriteLine("GOT SYNCABLE RPC");
    }
    
    public void TriggerRPC()
    {
        ExecuteRPC(_testRpc);
    }
}

partial class SyncableTestDerived : SyncableTest
{
    public SyncVar<int> IntVar2;
    public SyncVar<float> FloatVar2;
}

partial class BotLogic : AiControllerLogic<BasePlayer>
{
    private readonly SyncTimer _rotationChangeTimer = new SyncTimer(0.5f);
    
    public BotLogic(EntityParams entityParams) : base(entityParams)
    {
    }
}

[UpdateableEntity(true)]
partial class BasePlayer : PawnLogic
{
    [BindRpc(nameof(RpcMethod2), ExecuteFlags.SendToAll)]
    private static RemoteCall RpcTest2;
    public readonly SyncList<int> SyncLst = new SyncList<int>();
    private SyncVar<float> TestSyncVar2;
    public SyncVar<int> TestSyncVar;
    private readonly SyncString SyncStr = new SyncString();
    public readonly SyncString SyncStr2 = new SyncString();
    public SyncVar<long> TestSyncVar3;
    [BindRpc(nameof(RpcMethod1), ExecuteFlags.SendToAll)]
    private static RemoteCall RpcTest;
    public readonly SyncableTestDerived SyncTest = new SyncableTestDerived();

    public SyncVar<EntitySharedReference> SharedRefSyncvar;

    [SyncVarFlags(SyncFlags.Interpolated)]
    public SyncVar<float> FlagsTest1;
    
    [SyncVarFlags(SyncFlags.LagCompensated)]
    public SyncVar<float> FlagsTest2;
    
    [BindOnChange(nameof(OnFlagTest3Changed))]
    public SyncVar<float> FlagsTest3;

    [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
    public SyncVar<FloatAngle> FloatAngleTest;

    private void OnFlagTest3Changed(float prev)
    {
        //Console.WriteLine($"Flagstest3 changed {FlagsTest3}");
    }
    
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
            SyncTest.IntVar = 1;
            SyncTest.IntVar2 = 2;
            SyncTest.FloatVar = 3f;
            SyncTest.FloatVar2 = 4f;
            TestSyncVar.Value = 15;
        }
    }

    protected override void Update()
    {
        base.Update();
        if (EntityManager.IsServer)
        {
            FlagsTest3 += EntityManager.DeltaTimeF;
            SyncTest.TriggerRPC();
            //ExecuteRPC(RpcTest);
            ExecuteRPC(RpcTest2);
            if(EntityManager.Tick > 50)
                Destroy();
        }
        else
        {
            //Console.WriteLine($"CliDestroyed: {IsDestroyed}");
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        Console.WriteLine("Destroyed: " + (EntityManager.IsServer ? "server" : "client"));
    }

    private void RpcMethod1()
    {
        //Console.WriteLine($"1GOT {TestSyncVar} {SyncStr.Value} {SyncStr2.Value} {SyncTest.IntVar} {SyncTest.IntVar2} {SyncTest.FloatVar} {SyncTest.FloatVar2}");
    }
    
    private void RpcMethod2()
    {
        //Console.WriteLine($"2GOT {TestSyncVar} {SyncStr.Value} {SyncStr2.Value} {SyncTest.IntVar} {SyncTest.IntVar2} {SyncTest.FloatVar} {SyncTest.FloatVar2}");
    }
}

partial class BasePlayerTest : BasePlayer
{
    [BindRpc(nameof(BPRPC1))]
    private static RemoteCall BPTest1;
    public readonly SyncList<int> BPTest2 = new SyncList<int>();
    public SyncVar<int> BPTest3;
    public SyncVar<float> BPTest4;
    public readonly SyncString BPTest5 = new SyncString();
    public readonly SyncString BPTest6 = new SyncString();
    public SyncVar<long> BPTest7;
    [BindRpc(nameof(BPRPC2))]
    private static RemoteCall BPTest8;

    private void BPRPC1()
    {
        
    }

    private void BPRPC2()
    {
        
    }

    public BasePlayerTest(EntityParams entityParams) : base(entityParams)
    {
    }
}

partial class BasePlayerController : HumanControllerLogic<MyInput>
{
    public BasePlayerController(EntityParams entityParams) : base(entityParams)
    {
    }
    protected override void ReadInput(in MyInput input)
    {
        
    }

    protected override void GenerateInput(out MyInput input)
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
    public override int RoundTripTimeMs => 0;
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
        var playerEntity = sem.AddEntity<BasePlayerTest>();
        //var testPlayerEntity = sem.AddEntity<BasePlayerTest>();
        var playerController = sem.AddController<BasePlayerController>(player, e => e.StartControl(playerEntity));
        for (int i = 0; i < 1000; i++)
        {
            cem.Update();
            sem.Update();
            Thread.Sleep(1);
        }
    }
}