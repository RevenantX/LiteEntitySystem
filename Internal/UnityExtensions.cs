#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LiteEntitySystem.Internal
{
    internal sealed class EntitySystemBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private static readonly string GeneratedFilePath = Path.Combine(Application.dataPath, "LES_IL2CPP_AOT.cs");
        private static readonly string GeneratedMetaFilePath = Path.Combine(Application.dataPath, "LES_IL2CPP_AOT.cs.meta");
        
        private static readonly Type EntityLogicType = typeof(InternalEntity);
        private const BindingFlags BindFlags = BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly StringBuilder GenCode = new StringBuilder();
        private static readonly HashSet<(Type, Type)> AddedTypes = new HashSet<(Type, Type)>();
        private static readonly Dictionary<Type, string> KeywordTypeMap = new Dictionary<Type, string>()
        {
            { typeof(string), "string" },
            { typeof(sbyte), "sbyte" },
            { typeof(byte), "byte" },
            { typeof(short), "short"  },
            { typeof(ushort), "ushort"  },
            { typeof(int), "int"  },
            { typeof(uint), "uint" },
            { typeof(long), "long" },
            { typeof(ulong), "ulong"  },
            { typeof(char), "char"  },
            { typeof(float), "float"  },
            { typeof(double), "double"  },
            { typeof(bool), "bool" },
            { typeof(decimal), "decimal" }
        };

        public int callbackOrder => 0;

        public EntitySystemBuildProcessor()
        {
            File.Delete(GeneratedFilePath);
            File.Delete(GeneratedMetaFilePath);
        }

        private static string GetTypeName(Type type)
        {
            string fullName = type.Namespace == nameof(LiteEntitySystem) ? type.Name : type.FullName;
            bool isArray = type.IsArray && type.HasElementType;
            if (isArray)
                type = type.GetElementType();
            return KeywordTypeMap.TryGetValue(type, out var name) 
                ? name + (isArray ? "[]" : string.Empty)
                : fullName!.Replace('+', '.').Replace("`1", "<") + (type.IsGenericType ? $"{GetTypeName(type.GetGenericArguments()[0])}>" : "");
        }

        private static void AppendGenerator(Type classType, Type valueType)
        {
            if (!AddedTypes.Add((classType, valueType)))
                return;
            GenCode.Append(' ', 12);
            if(valueType == null)
                GenCode.AppendLine($"G.GenerateNoParams<{GetTypeName(classType)}>(null);");
            else if(valueType.IsArray)
                GenCode.AppendLine($"G.GenerateArray<{GetTypeName(classType)},{GetTypeName(valueType)}>(null);");
            else
                GenCode.AppendLine($"G.Generate<{GetTypeName(classType)},{GetTypeName(valueType)}>(null);");
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            AddedTypes.Clear();
            GenCode.Append(@$"//auto generated on {DateTime.UtcNow} UTC
namespace LiteEntitySystem.Internal
{{
    using G = MethodCallGenerator;
    public static class LES_IL2CPP_AOT
    {{
        public static void Methods()
        {{
");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var entity in assembly.GetTypes().Where(t =>
                             t != EntityLogicType &&
                             EntityLogicType.IsAssignableFrom(t)))
                {
                    foreach (var fieldInfo in entity.GetFields(BindFlags))
                    {
                        if (fieldInfo.GetCustomAttribute<SyncVar>() == null) 
                            continue;
                        
                        if (fieldInfo.FieldType.IsSubclassOf(typeof(SyncableField)))
                        {
                            foreach (var methodInfo in fieldInfo.FieldType.GetMethods(BindFlags))
                            {
                                if (methodInfo.GetCustomAttribute<SyncableRemoteCall>() != null)
                                {
                                    var parameters = methodInfo.GetParameters();
                                    AppendGenerator(fieldInfo.FieldType, parameters.Length == 0 ? null : parameters[0].ParameterType);
                                }
                            }
                        }
                        else
                        {
                            AppendGenerator(entity, fieldInfo.FieldType);
                        }
                    }
                    foreach (var methodInfo in entity.GetMethods(BindFlags))
                    {
                        if (methodInfo.GetCustomAttribute<RemoteCall>() != null)
                        {
                            var parameters = methodInfo.GetParameters();
                            AppendGenerator(entity, parameters.Length == 0 ? null : parameters[0].ParameterType);
                        }
                    }
                }
            }
            
            GenCode.Append("        }\n    }\n}\n");
            File.WriteAllText(GeneratedFilePath, GenCode.ToString().Replace("\r\n", "\n"));
            GenCode.Clear();
            AssetDatabase.Refresh();
        }
        
        public void OnPostprocessBuild(BuildReport report)
        {
            File.Delete(GeneratedFilePath);
            File.Delete(GeneratedMetaFilePath);
        }
    }
}

#endif