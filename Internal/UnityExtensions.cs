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
    internal sealed class EntitySystemBuildProcessor : IPreprocessBuildWithReport
    {
        private static readonly string GeneratedFilePath = Path.Combine(Application.dataPath, "LES_IL2CPP_AOT.cs");
        private static readonly string GeneratedMetaFilePath = GeneratedFilePath + ".meta";
        
        private static readonly Type EntityLogicType = typeof(InternalEntity);
        private const BindingFlags BindFlags = BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly StringBuilder GenCode = new StringBuilder();
        private static readonly HashSet<(Type, Type)> AddedTypes = new HashSet<(Type, Type)>();
        private static readonly HashSet<Type> AddedSizeofs = new HashSet<Type>();
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

        private static string GetTypeName(Type type)
        {
            string fullName = $"{type}";
            bool isArray = type.IsArray && type.HasElementType;
            if (isArray)
                type = type.GetElementType();
            if (type.IsGenericType)
            {
                fullName = fullName
                    .Substring(0, fullName.IndexOf("[", StringComparison.InvariantCulture))
                    .Replace("`1", $"<{GetTypeName(type.GetGenericArguments()[0])}>");
            }
            return KeywordTypeMap.TryGetValue(type, out var name) 
                ? name + (isArray ? "[]" : string.Empty)
                : fullName!.Replace('+', '.');
        }

        private static void AppendGenerator(Type classType, Type valueType)
        {
            if (!AddedTypes.Add((classType, valueType)))
                return;

            var valueTypeName = valueType != null ? GetTypeName(valueType) : string.Empty;
            var classTypeName = GetTypeName(classType);
            
            //Debug.Log($"vf: {(valueType != null ? valueType.Name : string.Empty)} vtn: {valueTypeName}, ctn: {classTypeName}");

            GenCode.Append(' ', 12);
            if(valueType == null)
                GenCode.AppendLine($"G.GenerateNoParams<{classTypeName}>(null);");
            else if(valueType.IsArray)
                GenCode.AppendLine($"G.GenerateArray<{classTypeName},{valueTypeName}>(null);");
            else
                GenCode.AppendLine($"G.Generate<{classTypeName},{valueTypeName}>(null);");
            if (valueType != null && valueType.IsValueType && AddedSizeofs.Add(valueType))
            {
                GenCode.Append(' ', 12);
                GenCode.AppendLine($"Unsafe.SizeOf<{valueTypeName}>();");
            }
        }
        
        [MenuItem("LiteEntitySystem/GenerateAOTCode")]
        private static void GenerateCode()
        {
            AddedTypes.Clear();
            AddedSizeofs.Clear();
            GenCode.Append(@$"//auto generated on {DateTime.UtcNow} UTC
using System.Runtime.CompilerServices;
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

        public void OnPreprocessBuild(BuildReport report)
        {
            GenerateCode();
        }
    }
}
#endif