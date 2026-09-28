using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public Object(IntPtr pointer) : base(pointer) { }
        public string name { get => throw null; set => throw null; }
        public HideFlags hideFlags { get => throw null; set => throw null; }
        public static bool operator ==(Object x, Object y) => throw null;
        public static bool operator !=(Object x, Object y) => throw null;
        public static void Destroy(Object obj) => throw null;
        public static void DontDestroyOnLoad(Object target) => throw null;
    }

    public class Component : Object
    {
        public Component(IntPtr pointer) : base(pointer) { }
        public Transform transform => throw null;
        public GameObject gameObject => throw null;
        public T GetComponent<T>() where T : Component => throw null;
    }

    public class Behaviour : Component
    {
        public Behaviour(IntPtr pointer) : base(pointer) { }
        public bool enabled { get => throw null; set => throw null; }
    }

    public class MonoBehaviour : Behaviour
    {
        public MonoBehaviour(IntPtr pointer) : base(pointer) { }
        public Coroutine StartCoroutine(Il2CppSystem.Collections.IEnumerator routine) => throw null;
    }

    public sealed class GameObject : Object
    {
        public GameObject(IntPtr pointer) : base(pointer) { }
        public GameObject(string name) : base(IntPtr.Zero) => throw null;
        public T AddComponent<T>() where T : Component => throw null;
        public T GetComponent<T>() => throw null;
        public Transform transform => throw null;
    }

    public class Transform : Component
    {
        public Transform(IntPtr pointer) : base(pointer) { }
        public Vector3 position { get => throw null; set => throw null; }
    }

    public class YieldInstruction : Il2CppSystem.Object
    {
        public YieldInstruction(IntPtr pointer) : base(pointer) { }
    }

    public sealed class Coroutine : YieldInstruction
    {
        public Coroutine(IntPtr pointer) : base(pointer) { }
    }

    public sealed class WaitForEndOfFrame : YieldInstruction
    {
        public WaitForEndOfFrame(IntPtr pointer) : base(pointer) { }
        public WaitForEndOfFrame() : base(IntPtr.Zero) => throw null;
    }

    public sealed class Camera : Behaviour
    {
        public Camera(IntPtr pointer) : base(pointer) { }
        public static Camera main => throw null;
        public Vector3 ScreenToWorldPoint(Vector3 position) => throw null;
        public Vector3 WorldToScreenPoint(Vector3 position) => throw null;
    }

    public sealed class Screen : Il2CppSystem.Object
    {
        public Screen(IntPtr pointer) : base(pointer) { }
        public static int width => throw null;
        public static int height => throw null;
    }

    public sealed class Time : Il2CppSystem.Object
    {
        public Time(IntPtr pointer) : base(pointer) { }
        public static float unscaledTime => throw null;
        public static float unscaledDeltaTime => throw null;
        public static int frameCount => throw null;
    }

    public class Texture : Object
    {
        public Texture(IntPtr pointer) : base(pointer) { }
    }

    public sealed class Texture2D : Texture
    {
        public Texture2D(IntPtr pointer) : base(pointer) { }
        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain) : base(IntPtr.Zero) => throw null;
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps) => throw null;
        public void ReadPixels(Rect source, int destX, int destY) => throw null;
        public Il2CppStructArray<byte> GetRawTextureData() => throw null;
    }

    public enum TextureFormat
    {
        RGB24 = 3,
        RGBA32 = 4,
    }

    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        DontSave = 52,
        HideAndDontSave = 61,
    }

    public enum KeyCode
    {
        None = 0,
        F1 = 282, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) => throw null;
    }

    public struct Vector2
    {
        public float x;
        public float y;
        public Vector2(float x, float y) => throw null;
        public static Vector2 zero => throw null;
        public static Vector2 up => throw null;
        public float sqrMagnitude => throw null;
        public Vector2 normalized => throw null;
        public void Normalize() => throw null;
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3(float x, float y, float z) => throw null;
    }
}
