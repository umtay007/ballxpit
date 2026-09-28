using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

public class Player : MonoBehaviour
{
    public Player(IntPtr pointer) : base(pointer) { }
    public static Player I { get => throw null; set => throw null; }
    public Il2CppReferenceArray<PlayerCharController> CharControllers => throw null;
    public Vector3 GetMouseWorldPos() => throw null;
    public Vector2 GetLastAimDir() => throw null;
}

public class PlayerCharController : MonoBehaviour
{
    public PlayerCharController(IntPtr pointer) : base(pointer) { }
    public Transform ShootXfm => throw null;
}

public class BallMgr : MonoBehaviour
{
    public BallMgr(IntPtr pointer) : base(pointer) { }
    public static BallMgr I => throw null;
    public Vector2 MousePosToAimDir(Vector3 mousePos, int idx) => throw null;
}
