#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LiteEntitySystem
{
    internal class EntitySystemBuildProcessor : IPreprocessBuildWithReport
    {
        private static readonly Type EntityLogicType = typeof(EntityManager.InternalEntity);
        private const BindingFlags MethodBindFlags = BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly StringBuilder GenCode = new StringBuilder();
        
        public int callbackOrder => 0;

        private static void AppendGenerator(Type entityType, Type valueType)
        {
            GenCode.Append(' ', 12);
            GenCode.AppendLine($"EntityManager.MethodCallGenerator.Generate<{entityType.FullName},{valueType.FullName}>(null);");
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            GenCode.Append(@$"//auto generated on {DateTime.UtcNow} UTC
namespace LiteEntitySystem
{{
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
                    foreach (var fieldInfo in entity.GetFields(MethodBindFlags))
                    {
                        if (fieldInfo.GetCustomAttribute<SyncVar>() == null) 
                            continue;
                        
                        if (fieldInfo.FieldType.IsSubclassOf(typeof(SyncableField)))
                        {
                            foreach (var methodInfo in fieldInfo.FieldType.GetMethods(MethodBindFlags))
                            {
                                if (methodInfo.GetCustomAttribute<SyncableRemoteCall>() != null)
                                    AppendGenerator(entity, methodInfo.GetParameters()[0].ParameterType);
                            }
                        }
                        else
                        {
                            AppendGenerator(entity, fieldInfo.FieldType);
                        }
                    }
                    foreach (var methodInfo in entity.GetMethods(MethodBindFlags))
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