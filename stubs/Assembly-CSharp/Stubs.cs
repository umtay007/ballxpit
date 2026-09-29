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
    public EnemyAttackResult Damage(float amt, PieceDmgType dmgType) => throw null;
    public void Heal(float amt) => throw null;
}

public enum PieceDmgType { kMelee, kArrow, kCannon, kSelf, kObstacle, kTouch, kVenom, kBurn, kNum }

public enum EnemyAttackResult { kHit, kMissed, kBlocked, kInvulnerable, kHeal, kNum }

public class BattleSaveData : Il2CppSystem.Object
{
    public BattleSaveData(IntPtr pointer) : base(pointer) { }
    public static BattleSaveData I { get => throw null; set => throw null; }
    public float CurHealth { get => throw null; set => throw null; }
}

// Real base class is Sirenix's SerializedMonoBehaviour; only the members matter here.
public class UpgradeMgr : MonoBehaviour
{
    public UpgradeMgr(IntPtr pointer) : base(pointer) { }
    public static UpgradeMgr I { get => throw null; set => throw null; }
    public int MaxHealth { get => throw null; set => throw null; }
}

public class TimeMgr : MonoBehaviour
{
    public TimeMgr(IntPtr pointer) : base(pointer) { }
    public static TimeMgr I { get => throw null; set => throw null; }
    public float GetTime() => throw null;
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
