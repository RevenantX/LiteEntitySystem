using System;
using System.Runtime.CompilerServices;
#if UNITY_2021_2_OR_NEWER
using UnityEngine;
#endif

namespace LiteNetLib.Utils
{
    public static class NetDataExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Put<T>(this NetDataWriter writer, T e) where T : unmanaged, Enum
        {
            switch (sizeof(T))
            {
                case 1: writer.Put(*(byte*)&e); break;
                case 2: writer.Put(*(short*)&e); break;
                case 4: writer.Put(*(int*)&e); break;
                case 8: writer.Put(*(long*)&e); break;
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Get<T>(this NetDataReader reader, out T result) where T : unmanaged, Enum
        {
            var e = default(T);
            switch (sizeof(T))
            {
                case 1: (*(byte*)&e) = reader.GetByte(); break;
                case 2: (*(short*)&e) = reader.GetShort(); break;
                case 4: (*(int*)&e) = reader.GetInt(); break;
                case 8: (*(long*)&e) = reader.GetLong(); break;
            }
            result = e;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T Get<T>(this NetDataReader reader) where T : unmanaged, Enum
        {
            var e = default(T);
            switch (sizeof(T))
            {
                case 1: (*(byte*)&e) = reader.GetByte(); break;
                case 2: (*(short*)&e) = reader.GetShort(); break;
                case 4: (*(int*)&e) = reader.GetInt(); break;
                case 8: (*(long*)&e) = reader.GetLong(); break;
            }
            return e;
        }
        
#if UNITY_2021_2_OR_NEWER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(this NetDataWriter writer, Vector3 v)
        {
            writer.Put(v.x);
            writer.Put(v.y);
            writer.Put(v.z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(this NetDataWriter writer, Vector2 v)
        {
            writer.Put(v.x);
            writer.Put(v.y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Put(this NetDataWriter writer, Quaternion q)
        {
            writer.Put(q.x);
            writer.Put(q.y);
            writer.Put(q.z);
            writer.Put(q.w);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Get(this NetDataReader reader, out Vector3 result)
        {
            result.x = reader.GetFloat();
            result.y = reader.GetFloat();
            result.z = reader.GetFloat();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Get(this NetDataReader reader, out Vector2 result)
        {
            result.x = reader.GetFloat();
            result.y = reader.GetFloat();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Get(this NetDataReader reader, out Quaternion result)
        {
            result.x = reader.GetFloat();
            result.y = reader.GetFloat();
            result.z = reader.GetFloat();
            result.w = reader.GetFloat();
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 GetVector3(this NetDataReader reader)
        {
            Vector3 v = new Vector3();
            v.x = reader.GetFloat();
            v.y = reader.GetFloat();
            v.z = reader.GetFloat();
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 GetVector2(this NetDataReader reader)
        {
            Vector2 v = new Vector2();
            v.x = reader.GetFloat();
            v.y = reader.GetFloat();
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion GetQuaternion(this NetDataReader reader)
        {
            Quaternion q = new Quaternion();
            q.x = reader.GetFloat();
            q.y = reader.GetFloat();
            q.z = reader.GetFloat();
            q.w = reader.GetFloat();
            return q;
        }
#endif
    }
}