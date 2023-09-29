#if UNITY_2021_2_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LiteNetLib.Utils;
using UnityEditor;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    [Serializable]
    public struct ResourceInfo : INetSerializable
    {
        public string FieldName;
        public string Path;
        
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(FieldName);
            writer.Put(Path);
        }

        public void Deserialize(NetDataReader reader)
        {
            FieldName = reader.GetString();
            Path = reader.GetString();
        }
    }
    
    public class SharedScriptableObject : ScriptableObject, INetSerializable
#if UNITY_EDITOR
        , ISerializationCallbackReceiver
#endif
    {
        [SerializeField, HideInInspector] private ResourceInfo[] _resourcePaths;
        [SerializeField, HideInInspector] private int _resourcesHash;
        [SerializeField, HideInInspector] private string _serializedName;
        public string ResourceName => _serializedName;
        private Type _type;
        
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
            newHash ^= name.GetHashCode();

            if (newHash != _resourcesHash)
            {
                _serializedName = name;
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
                field.SetValue(this, Resources.Load(resourceInfo.Path, field.FieldType));
            }
        }

        //INetSerializable
        public virtual void Serialize(NetDataWriter writer)
        {
            ushort resourcesCount = (ushort)(_resourcePaths?.Length ?? 0);
            writer.Put(resourcesCount);
            for (int i = 0; i < resourcesCount; i++)
                _resourcePaths![i].Serialize(writer);
            writer.Put(_serializedName);
        }

        public virtual void Deserialize(NetDataReader reader)
        {
            ushort resourcesCount = reader.GetUShort();
            _resourcePaths = new ResourceInfo[resourcesCount];
            for (int i = 0; i < resourcesCount; i++)
                _resourcePaths[i].Deserialize(reader);
            _serializedName = reader.GetString();
            LoadResources();
        }
    }
}
#endif