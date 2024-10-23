using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace LiteEntitySystemAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class LiteEntitySystemAnalyzer : DiagnosticAnalyzer
    {
        private const string DiagnosticId = "LES0001";
        private static readonly LocalizableString Title = GetResource(nameof(Resources.AnalyzerTitle));
        private static readonly LocalizableString MessageFormat = GetResource(nameof(Resources.AnalyzerMessageFormat));
        private static readonly LocalizableString Description = GetResource(nameof(Resources.AnalyzerDescription));
        private const string Category = "Rules";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterOperationAction(AnalyzeOperation, OperationKind.SimpleAssignment);
        }

        private static LocalizableString GetResource(string name) =>
            new LocalizableResourceString(name, Resources.ResourceManager, typeof(Resources));

        private static bool CheckTypes(ITypeSymbol sym1, ITypeSymbol sym2) =>
            SymbolEqualityComparer.IncludeNullability.Equals(sym1, sym2);

        private void AnalyzeOperation(OperationAnalysisContext context)
        {
            var assignmentOperation = (IAssignmentOperation)context.Operation;
            var syncVarSym = context.Compilation.GetTypeByMetadataName("LiteEntitySystem.SyncVar`1");

            if (assignmentOperation.Target is IFieldReferenceOperation field && CheckTypes(field.Type?.OriginalDefinition, syncVarSym))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, context.Operation.Syntax.GetLocation()));
            }
        }
    }
}