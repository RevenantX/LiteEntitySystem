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

            var fieldSaveIfDifferentInnerText = new StringBuilder();
            var fieldLoadIfDifferentInnerText = new StringBuilder();
            var fieldSaveInnerText = new StringBuilder();
            var fieldLoadInnerText = new StringBuilder();
            var fieldLoadHistoryInnerText = new StringBuilder();
            var fieldSetInterpolationInnerText = new StringBuilder();
            var fieldOnChangeInnerText = new StringBuilder();
            var syncablesResyncInnerText = new StringBuilder();
            var syncablesSetIdInnerText = new StringBuilder();
            var syncablesGetByIdText = new StringBuilder();
            var classMetadataText = new StringBuilder();
            
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
                    
                    fieldSaveIfDifferentInnerText.Clear();
                    fieldLoadIfDifferentInnerText.Clear();
                    fieldSaveInnerText.Clear();
                    fieldLoadInnerText.Clear();
                    fieldLoadHistoryInnerText.Clear();
                    fieldSetInterpolationInnerText.Clear();
                    fieldOnChangeInnerText.Clear();
                    syncablesResyncInnerText.Clear();
                    syncablesSetIdInnerText.Clear();
                    syncablesGetByIdText.Clear();
                    classMetadataText.Clear();
                    
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

                    string fieldIdString = InheritsFrom(syncableFieldType, classSymbol) ? "field.SyncableId - MetadataCache.BaseFieldIdCounter" : "field.Id - MetadataCache.BaseFieldIdCounter";
                    string internalAddition = classSymbol.ContainingNamespace.ToString().StartsWith("LiteEntitySystem") 
                        ? " internal" 
                        : string.Empty;

                    int fieldId = 0;
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
                            {
                                classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddRpcSyncable(ref {fieldSymbol.Name}, {methodCallGenerator});");
                            }
                            else
                            {
                                classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddRpc{spanString}<{className}{typeArgs}>(ref {fieldSymbol.Name}, {flags}, (e{typedRpc}) => e.{methodName}({typedRpcArg}), {methodCallGenerator});");
                            }
                            continue;
                        }
                        
                        string caseString = @$"
{TAB(4)}case {fieldId}:";
                        fieldSaveIfDifferentInnerText.Append(caseString);
                        fieldLoadIfDifferentInnerText.Append(caseString);
                        fieldSaveInnerText.Append(caseString);
                        fieldLoadInnerText.Append(caseString);
                        fieldLoadHistoryInnerText.Append(caseString);
                        fieldSetInterpolationInnerText.Append(caseString);
                        
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
                            fieldSaveIfDifferentInnerText.Append($" return CodeGenUtils.GetFieldManipulator({fieldName}).SaveIfDifferent(in field, data);");
                            fieldLoadIfDifferentInnerText.Append($" return CodeGenUtils.GetFieldManipulator({fieldName}).LoadIfDifferent(in field, data);");
                            fieldSaveInnerText.Append($" CodeGenUtils.GetFieldManipulator({fieldName}).Save(in field, data); break;");
                            fieldLoadInnerText.Append($" CodeGenUtils.GetFieldManipulator({fieldName}).Load(in field, data); break;");
                            fieldLoadHistoryInnerText.Append($" CodeGenUtils.GetFieldManipulator({fieldName}).LoadHistory(in field, tempHistory, historyA, historyB, lerpTime); break;");
                            fieldSetInterpolationInnerText.Append($" CodeGenUtils.GetFieldManipulator({fieldName}).SetInterpolation(in field, prev, current, fTimer); break;");
                            syncablesResyncInnerText.Append($"\n{TAB(3)}CodeGenUtils.OnSyncRequested({fieldSymbol.Name});");
                            syncablesSetIdInnerText.Append($"\n{TAB(4)}CodeGenUtils.InternalSyncablesSetup({fieldSymbol.Name}, this, (ushort)({syncableId}+classMetadata.BaseSyncablesCount));");
                            syncablesGetByIdText.Append($"\n{TAB(3)}case {syncableId}: return {fieldSymbol.Name};");
                            syncableId++;
                        }
                        else
                        {   
                            string hasChangeNotify = fieldSymbol.GetAttributes()
                                .Any(x => TypeEquals(x.AttributeClass, bindOnChangeAttribType))
                                ? "true"
                                : "false";
                            //here it should use parent syncflags
                            var syncVarGenericArg = ((INamedTypeSymbol)fieldSymbol.Type).TypeArguments[0];
                            //when SyncVar<T>
                            string dotValueText = classSymbol.TypeArguments.Contains(syncVarGenericArg) ? string.Empty : ".Value";
                            classMetadataText.AppendLine($"{TAB(4)}classMetadata.AddField<{syncVarGenericArg}>(\"{fieldSymbol.Name}\", {syncFlagsStr}, {hasChangeNotify});");

                            //skip local ids
                            if (TypeEquals(syncVarGenericArg, entitySharedRefType))
                                fieldSaveIfDifferentInnerText.Append(@$"
                    {{
                        var sharedRef = {fieldName}.Value;
                        if (sharedRef.IsLocal)
                            sharedRef = null;
                        if (sharedRef != Helpers.ReadStruct<{syncVarGenericArg}>(data))
                        {{
                            Helpers.WriteStruct(data, sharedRef);
                            return true;
                        }}
                        return false;
                    }}");
                            else
                                fieldSaveIfDifferentInnerText.Append(@$"   
                    if({fieldName}{dotValueText} != Helpers.ReadStruct<{syncVarGenericArg}>(data))       
                    {{
                        Helpers.WriteStruct(data, {fieldName}.Value);
                        return true;
                    }}
                    return false;");
                            
                            fieldLoadIfDifferentInnerText.Append(@$"
                    var {fieldSymbol.Name}Stored = Helpers.ReadStruct<{syncVarGenericArg}>(data);
                    if({fieldName}{dotValueText} != {fieldSymbol.Name}Stored)
                    {{
                        var old = {fieldName}.Value;
                        {fieldName}.Value = {fieldSymbol.Name}Stored;
                        Helpers.WriteStruct(data, old);
                        return true;
                    }}
                    return false;");
                            
                            fieldSaveInnerText.Append($" Helpers.WriteStruct(data, {fieldName}.Value); break;");
                            fieldLoadInnerText.Append($" Helpers.ReadStruct(data, out {fieldName}.Value); break;");
                            fieldLoadHistoryInnerText.Append(@$"        
                    tempHistory.WriteStruct({fieldName}.Value);
                    {fieldName}.Value = ValueTypeProcessor<{syncVarGenericArg}>.InterpDelegate != null
                        ? ValueTypeProcessor<{syncVarGenericArg}>.InterpDelegate(historyA.ReadStruct<{syncVarGenericArg}>(), historyB.ReadStruct<{syncVarGenericArg}>(), lerpTime)
                        : historyA.ReadStruct<{syncVarGenericArg}>();
                    break;");
                            fieldSetInterpolationInnerText.Append(@$"        
                    if(ValueTypeProcessor<{syncVarGenericArg}>.InterpDelegate == null) throw new Exception(""This type: {syncVarGenericArg} can't be interpolated"");
                    {fieldName}.Value = ValueTypeProcessor<{syncVarGenericArg}>.InterpDelegate(prev.ReadStruct<{syncVarGenericArg}>(), current.ReadStruct<{syncVarGenericArg}>(), fTimer);
                    break;");

                            var bindOnChangeAttr = fieldSymbol.GetAttributes().FirstOrDefault(x => TypeEquals(x.AttributeClass, bindOnChangeAttribType));
                            if (bindOnChangeAttr != null)
                            {
                                //TODO: error when signature differs
                                fieldOnChangeInnerText.Append($"{caseString} _target.{bindOnChangeAttr.ConstructorArguments[0].Value}(prevData.ReadStruct<{syncVarGenericArg}>()); break;");
                            }
                        }
                        
                        fieldId++;
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
                        : $"base.GetClassMetadata()";
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

            public override bool SaveIfDifferent(in EntityFieldInfo field, Span<byte> data)
            {{
                switch({fieldIdString})
                {{{fieldSaveIfDifferentInnerText}
                default: return base.SaveIfDifferent(in field, data);
                }}
            }}

            public override bool LoadIfDifferent(in EntityFieldInfo field, Span<byte> data)
            {{
                switch({fieldIdString})
                {{{fieldLoadIfDifferentInnerText}
                default: return base.LoadIfDifferent(in field, data);
                }}
            }}

            public override void Save(in EntityFieldInfo field, Span<byte> data)
            {{
                switch({fieldIdString})
                {{{fieldSaveInnerText}
                default: base.Save(in field, data); break;
                }}
            }}

            public override void Load(in EntityFieldInfo field, ReadOnlySpan<byte> data)
            {{
                switch({fieldIdString})
                {{{fieldLoadInnerText}
                default: base.Load(in field, data); break;
                }}
            }}

            public override void LoadHistory(in EntityFieldInfo field, Span<byte> tempHistory, ReadOnlySpan<byte> historyA, ReadOnlySpan<byte> historyB, float lerpTime)
            {{
                switch({fieldIdString})
                {{{fieldLoadHistoryInnerText}
                default: base.LoadHistory(in field, tempHistory, historyA, historyB, lerpTime); break;
                }}
            }}

            public override void SetInterpolation(in EntityFieldInfo field, ReadOnlySpan<byte> prev, ReadOnlySpan<byte> current, float fTimer)
            {{
                switch({fieldIdString})
                {{{fieldSetInterpolationInnerText}
                default: base.SetInterpolation(in field, prev, current, fTimer); break;
                }}
            }}

            public override void OnChange(in EntityFieldInfo field, ReadOnlySpan<byte> prevData)
            {{
                switch({fieldIdString})
                {{{fieldOnChangeInnerText}
                default: base.OnChange(in field, prevData); break;
                }}
            }}
        }}

        private FieldManipulator _fieldManipulator;
        private static GeneratedClassMetadata MetadataCache;


        protected{internalAddition} override FieldManipulator GetFieldManipulator()
        {{
            return _fieldManipulator ??= new InnerFieldManipulator(this);
        }}

        private bool _syncablesInitialized;

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