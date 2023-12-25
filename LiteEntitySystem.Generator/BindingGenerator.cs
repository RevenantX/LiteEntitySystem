using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LiteEntitySystem.Generator
{
    [Generator]
    public class BindingGenerator : ISourceGenerator
    {
        private static readonly StringBuilder ResultCode = new (); 
        
        private static readonly DiagnosticDescriptor NoPartialOnClass = new (
            id: "LES001",
            title: "Class derived from SyncableField or InternalEntity must be partial",
            messageFormat: "Class derived from SyncableField or InternalEntity must be partial '{0}'",
            category: "LiteEntitySystem",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        private static readonly DiagnosticDescriptor RemoteCallShouldBeStatic = new (
            id: "LES002",
            title: "RemoteCall should be static",
            messageFormat: "RemoteCall should be static '{0}'",
            category: "LiteEntitySystem",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        private static readonly DiagnosticDescriptor SyncableReadOnly = new (
            id: "LES003",
            title: "Syncable fields should be readonly",
            messageFormat: "Syncable fields should be readonly",
            category: "LiteEntitySystem",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        private static readonly DiagnosticDescriptor RemoteCallShouldHaveBind = new (
            id: "LES004",
            title: "RemoteCall should have [BindRpc] attribute",
            messageFormat: "RemoteCall should have [BindRpc] attribute '{0}'",
            category: "LiteEntitySystem",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        
        private static bool InheritsFrom(INamedTypeSymbol from, ITypeSymbol symbol)
        {
            while (true)
            {
                if (TypeEquals(symbol,from))
                {
                    return true;
                }
                if (symbol.BaseType != null)
                {
                    symbol = symbol.BaseType;
                    continue;
                }
                break;
            }
            return false;
        }

        private static bool TypeEquals(ISymbol x, ISymbol y)
        {
            return SymbolEqualityComparer.Default.Equals(x, y);
        }

        private static string TAB(int count)
        {
            return new string(' ', count*4);
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var compilation = context.Compilation;
            var baseSyncType = compilation.GetTypeByMetadataName("LiteEntitySystem.Internal.InternalSyncType");
            var internalEntityType = compilation.GetTypeByMetadataName("LiteEntitySystem.Internal.InternalEntity");
            var syncableFieldType = compilation.GetTypeByMetadataName("LiteEntitySystem.SyncableField");
            var bindOnChangeAttribType = compilation.GetTypeByMetadataName("LiteEntitySystem.BindOnChange");
            var bindRpcAttribType = compilation.GetTypeByMetadataName("LiteEntitySystem.BindRpc");
            var syncVarFlagsAttribType = compilation.GetTypeByMetadataName("LiteEntitySystem.SyncVarFlags");
            var localOnlyAttribType = compilation.GetTypeByMetadataName("LiteEntitySystem.LocalOnly");
            var updateableEntityAttribType = compilation.GetTypeByMetadataName("LiteEntitySystem.UpdateableEntity");
            var entitySharedRefType = compilation.GetTypeByMetadataName("LiteEntitySystem.EntitySharedReference");

            Func<IFieldSymbol, bool> correctSyncVarPredicate = x =>
                x.Type.Name == "SyncVar" || InheritsFrom(syncableFieldType, x.Type) || x.Type.Name.StartsWith("RemoteCall");

            var writeChangedDataText = new StringBuilder();
            var applyLagCompensationText = new StringBuilder();
            var syncablesResyncInnerText = new StringBuilder();
            var syncablesSetIdInnerText = new StringBuilder();
            var syncablesGetByIdText = new StringBuilder();
            var classMetadataText = new StringBuilder();

            var dumpInterpolatedText = new StringBuilder();
            var loadInterpolatedText = new StringBuilder();
            var dumpLagCompensatedText = new StringBuilder();
            var loadLagCompensatedText = new StringBuilder();
            var interpolateText = new StringBuilder();
            var loadPredictedText = new StringBuilder();
            var readChangedText = new StringBuilder();
            
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                foreach (var classDeclarationSyntax in syntaxTree.GetRoot().DescendantNodesAndSelf().OfType<ClassDeclarationSyntax>())
                {
                    var classSymbol = ModelExtensions.GetDeclaredSymbol(semanticModel, classDeclarationSyntax) as INamedTypeSymbol;
                    
                    //skip not entities
                    if(classSymbol == null || !InheritsFrom(baseSyncType, classSymbol))
                        continue;
                    
                    var currentSyncVars = classSymbol
                        .GetMembers()
                        .OfType<IFieldSymbol>()
                        .Where(correctSyncVarPredicate);
                    
                    //skip empty
                    if (!currentSyncVars.Any())
                        continue;
                    
                    if (!classDeclarationSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(NoPartialOnClass, classSymbol.Locations[0], classSymbol.Name));
                        continue;
                    }
                    
                    writeChangedDataText.Clear();
                    applyLagCompensationText.Clear();
                    syncablesResyncInnerText.Clear();
                    syncablesSetIdInnerText.Clear();
                    syncablesGetByIdText.Clear();
                    classMetadataText.Clear();
                    dumpInterpolatedText.Clear();
                    loadInterpolatedText.Clear();
                    dumpLagCompensatedText.Clear();
                    loadLagCompensatedText.Clear();
                    interpolateText.Clear();
                    loadPredictedText.Clear();
                    readChangedText.Clear();
                    
                    ResultCode.Clear();
                    ResultCode.AppendLine(@"// <auto-generated/>
using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem;
using LiteEntitySystem.Internal;");
                    
                    //classMetadataText.AppendLine(classSymbol.GetAttributes().Any(x => TypeEquals(x.AttributeClass, localOnlyAttribType))
                    //    ? $"{TAB(4)}classMetadata.IsLocalOnly = true;"
                    //    : $"{TAB(4)}classMetadata.IsLocalOnly = baseClassMetadata.IsLocalOnly;");

                    var updatableAttrib = classSymbol.GetAttributes()
                        .FirstOrDefault(x => TypeEquals(x.AttributeClass, updateableEntityAttribType));
                    if (updatableAttrib != null)
                    {
                        classMetadataText.AppendLine($"{TAB(4)}classMetadata.IsUpdateable = true;");
                        TypedConstant? arg = updatableAttrib.ConstructorArguments.FirstOrDefault();
                        if (arg.HasValue && arg.Value.Value != null)
                        {
                            classMetadataText.AppendLine($"{TAB(4)}classMetadata.UpdateOnClient = {arg.Value.Value.ToString().ToLower()};");
                        }
                        else
                        {
                            classMetadataText.AppendLine($"{TAB(4)}classMetadata.UpdateOnClient = false;");
                        }
                    }
                    else
                    {
                        classMetadataText.AppendLine($"{TAB(4)}classMetadata.IsUpdateable = baseClassMetadata.IsUpdateable;");
                        classMetadataText.AppendLine($"{TAB(4)}classMetadata.UpdateOnClient = baseClassMetadata.UpdateOnClient;");
                    }
                    
                    string genericArgs = string.Empty;
                    if (classSymbol.IsGenericType)
                    {
                        genericArgs = "<";
                        for(int i = 0; i < classSymbol.TypeArguments.Length; i++)
                        {
                            genericArgs += $"{classSymbol.TypeArguments[i]}";
                            if (i < classSymbol.TypeArguments.Length - 1)
                                genericArgs += ",";
                        }
                        genericArgs += ">";
                    }
                    string className = classSymbol.Name + genericArgs;
                    string internalAddition = classSymbol.ContainingNamespace.ToString().StartsWith("LiteEntitySystem") 
                        ? " internal" 
                        : string.Empty;
                    
                    int syncableId = 0;
                    
                    foreach (var fieldSymbol in currentSyncVars)
                    {
                        string fieldName = $"_target.{fieldSymbol.Name}";
                        if (fieldSymbol.Type.Name.StartsWith("RemoteCall"))
                        {
                            if (!fieldSymbol.IsStatic)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(RemoteCallShouldBeStatic, fieldSymbol.Locations[0], fieldSymbol.Name));
                                continue;
                            }
                            
                            if (!fieldSymbol.GetAttributes().Any(x => TypeEquals(x.AttributeClass, bindRpcAttribType)))
                            {
                                context.ReportDiagnostic(Diagnostic.Create(RemoteCallShouldHaveBind, fieldSymbol.Locations[0], fieldSymbol.Name));
                                continue;
                            }

                            var bindRpcAttrib = fieldSymbol.GetAttributes().First(x => TypeEquals(x.AttributeClass, bindRpcAttribType));
                            string methodName = bindRpcAttrib.ConstructorArguments[0].Value.ToString();
                            string flags = bindRpcAttrib.ConstructorArguments.Length > 1
                                ? "(ExecuteFlags)" + bindRpcAttrib.ConstructorArguments[1].Value
                                : "ExecuteFlags.SendToAll";

                            var namedFieldSym = (INamedTypeSymbol)fieldSymbol.Type;
                            string typeArgs = namedFieldSym.IsGenericType
                                ? $", {namedFieldSym.TypeArguments[0].Name}"
                                : string.Empty;
                            string typedRpc = namedFieldSym.IsGenericType
                                ? ", value"
                                : string.Empty;
                            string typedRpcArg = namedFieldSym.IsGenericType
                                ? "value"
                                : string.Empty;
                            string spanString = fieldSymbol.Type.Name.StartsWith("RemoteCallSpan")
                                ? "Span"
                                : string.Empty;
                            string methodCallGenerator = fieldSymbol.Type.Name.StartsWith("RemoteCallSpan")
                                ? $"(classPtr, buffer) => (({className})classPtr).{methodName}(MemoryMarshal.Cast<byte, {namedFieldSym.TypeArguments[0].Name}>(buffer))"
                                : namedFieldSym.IsGenericType
                                    ? $"(classPtr, buffer) => (({className})classPtr).{methodName}(Helpers.ReadStruct<{namedFieldSym.TypeArguments[0].Name}>(buffer))"
                                    : $"(classPtr, _) => (({className})classPtr).{methodName}()";

                            if (InheritsFrom(syncableFieldType, classSymbol))
                                classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddRpcSyncable(ref {fieldSymbol.Name}, {methodCallGenerator});");
                            else
                                classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddRpc{spanString}<{className}{typeArgs}>(ref {fieldSymbol.Name}, {flags}, (e{typedRpc}) => e.{methodName}({typedRpcArg}), {methodCallGenerator});");
                            continue;
                        }
                        
                        var syncVarFlagsAttr = fieldSymbol.GetAttributes().FirstOrDefault(x =>
                            TypeEquals(x.AttributeClass, syncVarFlagsAttribType));
                        string syncFlagsStr = syncVarFlagsAttr != null
                            ? "(SyncFlags)" + syncVarFlagsAttr.ConstructorArguments.First().Value
                            : "SyncFlags.None";
                        
                        //if syncableField
                        if (InheritsFrom(syncableFieldType, fieldSymbol.Type))
                        {
                            if (!fieldSymbol.IsReadOnly)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(SyncableReadOnly, fieldSymbol.Locations[0]));
                            }
                            classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddSyncableField(CodeGenUtils.GetMetadata({fieldSymbol.Name}), {syncFlagsStr});");
                            writeChangedDataText.Append($@"
                CodeGenUtils.GetFieldManipulator({fieldName}).WriteChanged(ref fieldsData);");
                            loadPredictedText.Append($@"
                data = data.Slice(CodeGenUtils.GetFieldManipulator({fieldName}).LoadPredicted(data));");
                            readChangedText.Append(@$"
                CodeGenUtils.GetFieldManipulator({fieldName}).ReadChanged(ref fieldsData);");
                            syncablesResyncInnerText.Append($"\n{TAB(3)}CodeGenUtils.OnSyncRequested({fieldSymbol.Name});");
                            syncablesSetIdInnerText.Append($"\n{TAB(4)}CodeGenUtils.InternalSyncablesSetup({fieldSymbol.Name}, this, (ushort)({syncableId}+classMetadata.BaseSyncablesCount));");
                            syncablesGetByIdText.Append($"\n{TAB(3)}case {syncableId}: return {fieldSymbol.Name};");
                            syncableId++;
                        }
                        else
                        {
                            var bindOnChangeAttr = fieldSymbol.GetAttributes().FirstOrDefault(x => TypeEquals(x.AttributeClass, bindOnChangeAttribType));
                            //here it should use parent syncflags
                            var syncVarGenericArg = ((INamedTypeSymbol)fieldSymbol.Type).TypeArguments[0];
                            //when SyncVar<T>
                            string dotValueText = classSymbol.TypeArguments.Contains(syncVarGenericArg) ? string.Empty : ".Value";
                            classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddField<{syncVarGenericArg}>(\"{fieldSymbol.Name}\", {syncFlagsStr});");

                            //skip local ids
                            if (TypeEquals(syncVarGenericArg, entitySharedRefType))
                                writeChangedDataText.Append(@$"
                {{
                    var sharedRef = {fieldName}.Value;
                    if (sharedRef.IsLocal)
                        sharedRef = null;
                    if (sharedRef != Helpers.ReadStruct<{syncVarGenericArg}>(fieldsData.Data))
                    {{
                        WriteStruct(ref fieldsData.Data, sharedRef);
                        fieldsData.MarkChanged();
                    }}
                    else
                    {{
                        SliceBySize<{syncVarGenericArg}>(ref fieldsData.Data);
                        fieldsData.MarkUnchanged();
                    }}
                }}");
                            else
                                writeChangedDataText.Append(@$"   
                if({fieldName}{dotValueText} != Helpers.ReadStruct<{syncVarGenericArg}>(fieldsData.Data))       
                {{
                    WriteStruct(ref fieldsData.Data, {fieldName}.Value);
                    fieldsData.MarkChanged();
                }}
                else
                {{
                    SliceBySize<{syncVarGenericArg}>(ref fieldsData.Data);
                    fieldsData.MarkUnchanged();
                }}");
                            
                            readChangedText.Append(@"
                if (fieldsData.ContainsField())
                {");
                            
                            int syncFlags = 0;
                            bool isPredicted = false;
                            bool isInterpolated = false;
                            if (syncVarFlagsAttr != null && int.TryParse(syncVarFlagsAttr.ConstructorArguments.First().Value.ToString(), out syncFlags))
                            {
                                //interpolated
                                if ((syncFlags & 1) != 0)
                                {
                                    dumpInterpolatedText.Append(@$"
                WriteStruct(ref data, {fieldName}.Value);");
                                    loadInterpolatedText.Append(@$"
                ReadStruct(ref data, out {fieldName}.Value);");
                                    interpolateText.Append(@$"
                InterpolateStruct(ref prev, ref current, fTimer, out {fieldName}.Value);");
                                    readChangedText.Append(@$"
                    if(fieldsData.WriteInterpolationData)
                        WriteStruct(ref fieldsData.InterpolatedData, {fieldName}.Value);");
                                    isInterpolated = true;
                                }
                                //lag compensated
                                if ((syncFlags & 2) != 0)
                                {
                                    dumpLagCompensatedText.Append(@$"
                WriteStruct(ref data, {fieldName}.Value);");
                                    loadLagCompensatedText.Append(@$"
                ReadStruct(ref data, out {fieldName}.Value);");
                                    if (isInterpolated)
                                    {
                                        applyLagCompensationText.Append(@$"        
                WriteStruct(ref tempHistory, {fieldName}.Value);
                {fieldName}.Value = ValueTypeProcessor<{syncVarGenericArg}>.InterpDelegate(historyA.ReadStruct<{syncVarGenericArg}>(), historyB.ReadStruct<{syncVarGenericArg}>(), lerpTime);
                SliceBySize<{syncVarGenericArg}>(ref historyA);
                SliceBySize<{syncVarGenericArg}>(ref historyB);");
                                    }
                                    else
                                    {
                                        applyLagCompensationText.Append(@$"        
                WriteStruct(ref tempHistory, {fieldName}.Value);
                ReadStruct(ref historyA, out {fieldName}.Value);
                SliceBySize<{syncVarGenericArg}>(ref historyB);");
                                    }
                                }
                                //predicted if always rollback
                                if ((syncFlags & (1<<4)) != 0)
                                {
                                    loadPredictedText.Append(@$"
                ReadStruct(ref data, out {fieldName}.Value);");
                                    isPredicted = true;
                                }
                            }
                            //also if field doesn't have OnlyForOtherPlayers or NeverRollBack
                            if (!isPredicted && (syncFlags & ((1<<2)|(1<<5))) == 0)
                            {
                                if (InheritsFrom(syncableFieldType, classSymbol))
                                {
                                    loadPredictedText.Append(@$"
                ReadStruct(ref data, out {fieldName}.Value);");  
                                }
                                else
                                {
                                    loadPredictedText.Append(@$"
                data = data.Slice(_target.IsRemoteControlled ? Helpers.SizeOfStruct<{syncVarGenericArg}>() : Helpers.ReadStructAndReturnSize(data, out {fieldName}.Value));");                            
                                }

                                isPredicted = true;
                            }

                            if (isPredicted)
                            {
                                readChangedText.Append(@$"
                    WriteStruct(ref fieldsData.PredictedData, {fieldName}.Value);");
                            }


                            if (bindOnChangeAttr != null)
                            {
                                //TODO: error when signature differs
                                //load if differenct
                                readChangedText.Append(@$"
                    var {fieldSymbol.Name}Stored = Helpers.ReadStruct<{syncVarGenericArg}>(fieldsData.RawData);
                    if({fieldName}{dotValueText} != {fieldSymbol.Name}Stored)
                    {{
                        Helpers.WriteStruct(fieldsData.RawData, {fieldName}.Value);
                        {fieldName}.Value = {fieldSymbol.Name}Stored;
                        fieldsData.BindOnChange(
                            (e, d) => {{ (({className})e).{bindOnChangeAttr.ConstructorArguments[0].Value}(d.ReadStruct<{syncVarGenericArg}>()); }}, 
                            _target, 
                            fieldsData.ReaderPosition + (fieldsData.InitialLength - fieldsData.RawData.Length));
                    }}
                    SliceBySize<{syncVarGenericArg}>(ref fieldsData.RawData);");
                            }
                            else
                            {
                                readChangedText.Append(@$"
                    ReadStruct(ref fieldsData.RawData, out {fieldName}.Value);");
                            }
                            readChangedText.Append(@"
                }
                else
                {");
                            if (isPredicted)
                            {
                                readChangedText.Append($@"
                    SliceBySize<{syncVarGenericArg}>(ref fieldsData.PredictedData);");
                            }
                            if (isInterpolated)
                            {
                                readChangedText.Append($@"
                    SliceBySize<{syncVarGenericArg}>(ref fieldsData.InterpolatedData);");
                            }
                            
                            readChangedText.Append(@"
                }");
                        }
                    }

                    string syncablesPart = InheritsFrom(syncableFieldType, classSymbol) || TypeEquals(internalEntityType, classSymbol)
                        ? string.Empty
                        : $@"
        protected{internalAddition} override SyncableField InternalGetSyncableFieldById(int id)
        {{
            switch(id-MetadataCache.BaseSyncablesCount)
            {{{syncablesGetByIdText}
            default: return base.InternalGetSyncableFieldById(id);
            }}
        }} 

        protected{internalAddition} override void InternalSyncablesResync() 
        {{
            base.InternalSyncablesResync();{syncablesResyncInnerText}
        }}";
                    if (!classSymbol.ContainingNamespace.IsGlobalNamespace)
                        ResultCode.Append($@"
namespace {classSymbol.ContainingNamespace}
{{");

                    bool baseTypeIsSimple =
                        TypeEquals(classSymbol.BaseType, syncableFieldType) ||
                        TypeEquals(classSymbol.BaseType, baseSyncType);
                    
                    string baseFieldManipulator = baseTypeIsSimple
                        ? string.Empty
                        : $"{classSymbol.BaseType.Name}.Inner";
                    string baseConstructorCall = baseTypeIsSimple
                        ? string.Empty
                        : " : base(target)";
                    string baseclassMetadata = baseTypeIsSimple
                        ? "new GeneratedClassMetadata()"
                        : "base.GetClassMetadata()";
                    string newAddition = baseTypeIsSimple
                        ? string.Empty
                        : "new ";

                    ResultCode.Append($@"

    partial class {className}
    {{
        protected {newAddition}class InnerFieldManipulator : {baseFieldManipulator}FieldManipulator
        {{
            private {className} _target;

            public InnerFieldManipulator({className} target){baseConstructorCall}
            {{
                _target = target;
            }}

            public override int DumpInterpolated(Span<byte> data) 
            {{
                var origDataSize = data.Length;
                data = data.Slice(base.DumpInterpolated(data));{dumpInterpolatedText}
                return origDataSize - data.Length;
            }}

            public override int LoadInterpolated(ReadOnlySpan<byte> data)
            {{
                var origDataSize = data.Length;
                data = data.Slice(base.LoadInterpolated(data));{loadInterpolatedText}
                return origDataSize - data.Length;
            }}

            public override int Interpolate(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer)
            {{
                var origDataSize = prev.Length;
                var size = base.Interpolate(prev, current, fTimer);
                prev = prev.Slice(size);
                current = current.Slice(size);{interpolateText}
                return origDataSize - prev.Length;
            }}

            public override int DumpLagCompensated(Span<byte> data)
            {{
                var origDataSize = data.Length;
                data = data.Slice(base.DumpLagCompensated(data));{dumpLagCompensatedText}
                return origDataSize - data.Length;
            }}

            public override int LoadLagCompensated(ReadOnlySpan<byte> data)
            {{
                var origDataSize = data.Length;
                data = data.Slice(base.LoadLagCompensated(data));{loadLagCompensatedText}
                return origDataSize - data.Length;
            }}

            public override int ApplyLagCompensation(Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime)
            {{
                var origDataSize = historyA.Length;
                var size = base.ApplyLagCompensation(tempHistory, historyA, historyB, lerpTime);
                tempHistory = tempHistory.Slice(size);
                historyA = historyA.Slice(size);
                historyB = historyB.Slice(size);{applyLagCompensationText}
                return origDataSize - historyA.Length;
            }}

            public override int LoadPredicted(ReadOnlySpan<byte> data)
            {{
                var origDataSize = data.Length;
                data = data.Slice(base.LoadPredicted(data));{loadPredictedText}
                return origDataSize - data.Length;
            }}

            public override void ReadChanged(ref DeltaFieldsData fieldsData)
            {{
                base.ReadChanged(ref fieldsData);{readChangedText}
            }}

            public override void WriteChanged(ref WriteFieldsData fieldsData)
            {{
                base.WriteChanged(ref fieldsData);{writeChangedDataText}
            }}
        }}

        private bool _syncablesInitialized;
        private FieldManipulator _fieldManipulator;
        private static GeneratedClassMetadata MetadataCache;

        protected{internalAddition} override FieldManipulator GetFieldManipulator()
        {{
            return _fieldManipulator ??= new InnerFieldManipulator(this);
        }}

        protected{internalAddition} override GeneratedClassMetadata GetClassMetadata()
        {{
            ref var classMetadata = ref GeneratedClassDataHandler<{className}>.ClassMetadata;
            if (classMetadata == null)
            {{
                var baseClassMetadata = {baseclassMetadata};
                classMetadata = new GeneratedClassMetadata(baseClassMetadata);
                MetadataCache = classMetadata;
                _syncablesInitialized = true;
{syncablesSetIdInnerText}
{classMetadataText}
                classMetadata.Init();
            }}
            else if(!_syncablesInitialized)
            {{
                _syncablesInitialized = true;
{syncablesSetIdInnerText}
            }}
            return classMetadata;
        }}
{syncablesPart}
    }}
");
                    if (!classSymbol.ContainingNamespace.IsGlobalNamespace)
                        ResultCode.AppendLine("}");
                    else
                        ResultCode.AppendLine();
                    
                    context.AddSource($"{classSymbol.Name}.g.cs", ResultCode.ToString());
                }
            }
        }

        public void Initialize(GeneratorInitializationContext context) { }
    }
}