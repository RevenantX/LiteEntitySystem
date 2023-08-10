#if UNITY_2021_2_OR_NEWER
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace LiteEntitySystem.Extensions
{
    public class SyncScriptableObject<T> : SyncNetSerializable<T> where T : ScriptableObject, INetSerializable
    {
        private static readonly Func<T> Constructor = ScriptableObject.CreateInstance<T>;
        
        public SyncScriptableObject() : base(Constructor)
        {
            
        }
    }
}
#endif