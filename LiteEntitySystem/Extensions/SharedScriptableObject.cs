#if UNITY_2021_2_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    [Serializable]
    public struct ResourceInfo
    {
        public string FieldName;
        public string Path;
    }
    
    public class SharedScriptableObject : ScriptableObject
#if UNITY_EDITOR
        , ISerializationCallbackReceiver
#endif
    {
        [SerializeField, HideInInspector] private ResourceInfo[] _resourcePaths;
        [SerializeField, HideInInspector] private int _resourcesHash;
        private Type _type;
        private static readonly Type SpriteType = typeof(Sprite);
        
#if UNITY_EDITOR
        private FieldInfo[] _fields;
        private static readonly Type ResourceType = typeof(UnityEngine.Object);
        private static readonly ThreadLocal<List<ResourceInfo>> ResourceInfoCache = new(() => new List<ResourceInfo>());
        private const string ResourcesPath = "Assets/Resources/";
        
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            int newHash = 0;
            var resourceInfoCache = ResourceInfoCache.Value;
            resourceInfoCache.Clear();
            _type ??= GetType();
            _fields ??= _type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach(var field in _fields)
            {
                if(!field.FieldType.IsSubclassOf(ResourceType))
                    continue;
                var fieldValue = field.GetValue(this);
                if(fieldValue == null)
                    continue;
                string assetPath = AssetDatabase.GetAssetPath((UnityEngine.Object)fieldValue);
                if(string.IsNullOrEmpty(assetPath))
                    continue;
                if (!assetPath.StartsWith(ResourcesPath))
                {
                    Debug.LogWarning($"Resource in {_type.Name} ({field.Name}) is not in Resources path: \"{assetPath}\"");
                    continue;
                }
                int lastPointPosition = assetPath.LastIndexOf(".", StringComparison.InvariantCulture);
                assetPath = lastPointPosition != -1 
                    ? assetPath.Substring(ResourcesPath.Length, lastPointPosition - ResourcesPath.Length) 
                    : assetPath.Substring(ResourcesPath.Length);
               
                resourceInfoCache.Add(new ResourceInfo { FieldName = field.Name, Path = assetPath });
                newHash ^= assetPath.GetHashCode();
            }

            if (newHash != _resourcesHash)
            {
                _resourcesHash = newHash;
                _resourcePaths = resourceInfoCache.ToArray();
                EditorUtility.SetDirty(this);
            }
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {

        }
#endif
        
        internal void LoadResources()
        {
            if (_resourcePaths == null || _resourcePaths.Length == 0)
                return;
            _type ??= GetType();
            foreach (var resourceInfo in _resourcePaths)
            {
                var field = _type.GetField(resourceInfo.FieldName);
                //Debug.Log($"Load resource: {resourceInfo.FieldName} - {resourceInfo.Path}");
                if (field.FieldType == SpriteType)
                {
                    field.SetValue(this, Resources.Load<Sprite>(resourceInfo.Path));
                }
                else
                {
                    field.SetValue(this, Resources.Load(resourceInfo.Path));
                }
            }
        }
    }
}
#endif