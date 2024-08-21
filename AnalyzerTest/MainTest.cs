using System.Reflection;
using LiteEntitySystem.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    LiteEntitySystemAnalyzer.LiteEntitySystemAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;
using Test = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<
    LiteEntitySystemAnalyzer.LiteEntitySystemAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace AnalyzerTest;

[TestClass]
public class MainTest
{
    private static async Task TestAnlyzer(string text, DiagnosticResult expected)
    {
        var t = new Test
        {
            TestState =
            {
                Sources = { text },
                ExpectedDiagnostics = { expected },
                AdditionalReferences = { MetadataReference.CreateFromFile(typeof(InternalEntity).GetTypeInfo().Assembly.Location) }
            }
        };
        await t.RunAsync();
    }
    
    [TestMethod]
    public async Task LocalIntCouldBeConstant_Diagnostic()
    {
        await TestAnlyzer(@"
using System;
using LiteEntitySystem;

class Program
{
    public static SyncVar<int> sv;

    static void Main()
    {   
        sv = new SyncVar<int>();
    }
}
", Verify.Diagnostic().WithLocation(1, 7));
    }
}