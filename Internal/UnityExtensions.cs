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
    internal class EntitySystemBuildProcessor : IPreprocessBuildWithReport
    {
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

        private static string GetTypeName(Type type)
        {
            if( type.Namespace == nameof(LiteEntitySystem) )
                return type.Name;
            bool isArray = type.IsArray && type.HasElementType;
            if (isArray)
                type = type.GetElementType();
            return KeywordTypeMap.TryGetValue(type, out var name) 
                ? name + (isArray ? "[]" : string.Empty)
                : type.FullName!.Replace('+', '.');
        }

        private static void AppendGenerator(Type entityType, Type valueType)
        {
            if (!AddedTypes.Add((entityType, valueType)))
                return;
            GenCode.Append(' ', 12);
            GenCode.AppendLine($"G.Generate<{GetTypeName(entityType)},{GetTypeName(valueType)}>(null);");
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
                                    AppendGenerator(fieldInfo.FieldType, methodInfo.GetParameters()[0].ParameterType);
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
                            AppendGenerator(entity, methodInfo.GetParameters()[0].ParameterType);
                    }
                }
            }

            if(!AssetDatabase.IsValidFolder("Assets/Code"))
                AssetDatabase.CreateFolder("Assets", "Code");
            else
                AssetDatabase.DeleteAsset("Assets/Code/Generated");
            AssetDatabase.CreateFolder("Assets/Code", "Generated");
            GenCode.Append("        }\n    }\n}\n");
            File.WriteAllText(Path.Combine(Application.dataPath, $"Code/Generated/LES_IL2CPP_AOT.cs"), GenCode.ToString().Replace("\r\n", "\n"));
            GenCode.Clear();
            AssetDatabase.Refresh();
        }
    }
}

#endif