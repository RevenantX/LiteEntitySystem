using LiteEntitySystem;

public class Program
{
    public SyncVar<int> Sv;
    public static SyncVar<int> StaticSV;
    
    public static void Main(string[] args)
    {
        Program p = new Program();
        p.Sv = new SyncVar<int>();
        StaticSV = new SyncVar<int>();
    }
}