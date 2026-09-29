using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Collections.Generic;
using UnityEngine;

public class Player : MonoBehaviour
{
    public Player(IntPtr pointer) : base(pointer) { }
    public static Player I { get => throw null; set => throw null; }
    public Il2CppReferenceArray<PlayerCharController> CharControllers => throw null;
    public Vector3 GetMouseWorldPos() => throw null;
    public Vector2 GetLastAimDir() => throw null;
    public Vector2 _lastAimDir { get => throw null; set => throw null; }
    public EnemyAttackResult Damage(float amt, PieceDmgType dmgType) => throw null;
    public void Heal(float amt) => throw null;
    public void SetPos(Vector3 pos, bool snap = false) => throw null;
}

public enum PieceDmgType { kMelee, kArrow, kCannon, kSelf, kObstacle, kTouch, kVenom, kBurn, kNum }

public enum EnemyAttackResult { kHit, kMissed, kBlocked, kInvulnerable, kHeal, kNum }

public class BattleSaveData : Il2CppSystem.Object
{
    public BattleSaveData(IntPtr pointer) : base(pointer) { }
    public static BattleSaveData I { get => throw null; set => throw null; }
    public float CurHealth { get => throw null; set => throw null; }
    public List<HeroInst> Heroes { get => throw null; set => throw null; }
    public List<PassiveInst> Passives { get => throw null; set => throw null; }
    public List<UpgradeInfo> BanishedItems { get => throw null; set => throw null; }
    public CharBattleInst CurChar { get => throw null; set => throw null; }
    public LevelType CurLevel { get => throw null; set => throw null; }
    public int CurDifficulty { get => throw null; set => throw null; }
    public int CurNGPlusLvl { get => throw null; set => throw null; }
    public int CurTurn { get => throw null; set => throw null; }
    public int NumKills { get => throw null; set => throw null; }
    public float CurXP { get => throw null; set => throw null; }
    public List<GridPieceInst> Pieces { get => throw null; set => throw null; }
    public List<PickupInst> Pickups { get => throw null; set => throw null; }
    public int Seed { get => throw null; set => throw null; }
    public int UpgradeLvl { get => throw null; set => throw null; }
    public int LastFissionCount { get => throw null; set => throw null; }
    public int GetCurTurnSeed() => throw null;
    public int GetCurLvlSeed() => throw null;
}

// Real base class is Sirenix's SerializedMonoBehaviour; only the members matter here.
public class UpgradeMgr : MonoBehaviour
{
    public UpgradeMgr(IntPtr pointer) : base(pointer) { }
    public static UpgradeMgr I { get => throw null; set => throw null; }
    public int MaxHealth { get => throw null; set => throw null; }
    public DelegateUtl.NoArgsEvent OnHeroesChanged { get => throw null; set => throw null; }
    public DelegateUtl.NoArgsEvent OnPassivesChanged { get => throw null; set => throw null; }
    public void CalculateStats() => throw null;
    public void ApplyUpgrade(UpgradeChoice c) => throw null;
    public void AddHero(HeroType ht, int evoIdx = 0) => throw null;
    public void CombineHeroes(HeroCombo combo) => throw null;
}

// Real base class is Il2CppSystem.Object; a static class can't say so in C#.
public static class DelegateUtl
{
    public sealed class NoArgsEvent : Il2CppSystem.MulticastDelegate
    {
        public NoArgsEvent(IntPtr pointer) : base(pointer) { }
        public void Invoke() => throw null;
    }
}

public enum UpgradeType { kHero, kPassive, kPet, kPetUpgrade }

public enum HeroType { }

public enum PassiveType { }

public enum LevelUpType { kNormal, kFuser, kBonusBall, kBonusPassive, kNum }

// Real base class is Sirenix's SerializedScriptableObject.
public class UpgradeInfo : UnityEngine.Object
{
    public UpgradeInfo(IntPtr pointer) : base(pointer) { }
    public string Name { get => throw null; set => throw null; }
    public string GetNameSlug() => throw null;
    public virtual UpgradeType GetUpgradeType() => throw null;
}

public class HeroInfo : UpgradeInfo
{
    public HeroInfo(IntPtr pointer) : base(pointer) { }
    public HeroType Type { get => throw null; set => throw null; }
    public Color BallColor { get => throw null; set => throw null; }
    public bool IsUnlocked() => throw null;
    public bool CanBeUsed() => throw null;
}

public class PassiveInfo : UpgradeInfo
{
    public PassiveInfo(IntPtr pointer) : base(pointer) { }
    public PassiveType Type { get => throw null; set => throw null; }
    public Color MainColor { get => throw null; set => throw null; }
    public bool IsUnlocked() => throw null;
    public bool CanBeUsed() => throw null;
}

public class UpgradeInst<T> : Il2CppSystem.Object where T : UpgradeInfo
{
    public UpgradeInst(IntPtr pointer) : base(pointer) { }
    public int Lvl { get => throw null; set => throw null; }
    public virtual T GetInfo() => throw null;
    public virtual bool CanUpgrade() => throw null;
}

public class HeroInst : UpgradeInst<HeroInfo>
{
    public HeroInst(IntPtr pointer) : base(pointer) { }
    public HeroInst(HeroInst toCopy) : base(IntPtr.Zero) => throw null;
    public HeroType Type { get => throw null; set => throw null; }
    public BallObj Obj { get => throw null; set => throw null; }
    public override HeroInfo GetInfo() => throw null;
    public bool HasType(HeroType ht) => throw null;
    public bool CanCombine(HeroInst h2) => throw null;
    public bool IsBadCombo(HeroInst h2) => throw null;
}

public class PassiveInst : UpgradeInst<PassiveInfo>
{
    public PassiveInst(IntPtr pointer) : base(pointer) { }
    public PassiveInst(PassiveType type) : base(IntPtr.Zero) => throw null;
    public PassiveInst(PassiveInst toCopy) : base(IntPtr.Zero) => throw null;
    public PassiveType Type { get => throw null; set => throw null; }
    public override PassiveInfo GetInfo() => throw null;
}

// A struct in the game; Il2CppInterop wraps it as a class.
public sealed class UpgradeChoice : Il2CppSystem.Object
{
    public UpgradeChoice(IntPtr pointer) : base(pointer) { }
    public UpgradeChoice(UpgradeInfo inf, bool isNew) : base(IntPtr.Zero) => throw null;
    public UpgradeChoice(UpgradeType t, int eqIdx, UpgradeInfo inf, bool isNew, int evoIdx = 0) : base(IntPtr.Zero) => throw null;
    public UpgradeType Type { get => throw null; set => throw null; }
    public int EquipmentIdx { get => throw null; set => throw null; }
    public int EvoIdx { get => throw null; set => throw null; }
    public UpgradeInfo Info { get => throw null; set => throw null; }
    public bool IsNew { get => throw null; set => throw null; }
}

// A blittable struct: Il2CppInterop keeps it a struct.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct HeroCombo
{
    [System.Runtime.InteropServices.FieldOffset(0)] public int Idx1;
    [System.Runtime.InteropServices.FieldOffset(4)] public HeroType H1;
    [System.Runtime.InteropServices.FieldOffset(8)] public int Idx2;
    [System.Runtime.InteropServices.FieldOffset(12)] public HeroType H2;
    public HeroCombo(int idx1, HeroType h1, int idx2, HeroType h2) => throw null;
}

// Real base class is FastPooledObject.
public class BallObj : MonoBehaviour
{
    public BallObj(IntPtr pointer) : base(pointer) { }
}

// Real base class is Sirenix's SerializedMonoBehaviour.
public class InfoDB : MonoBehaviour
{
    public InfoDB(IntPtr pointer) : base(pointer) { }
    public static InfoDB I { get => throw null; set => throw null; }
    public Il2CppReferenceArray<HeroInfo> BaseHeroes { get => throw null; set => throw null; }
    public Il2CppReferenceArray<PassiveInfo> Passives { get => throw null; set => throw null; }
}

// Real base class is CoolSelectable.
public class OverlayUI : MonoBehaviour
{
    public OverlayUI(IntPtr pointer) : base(pointer) { }
    public static List<OverlayUI> sOverlayStack { get => throw null; set => throw null; }
}

public class LevelUpUI : OverlayUI
{
    public LevelUpUI(IntPtr pointer) : base(pointer) { }
    public static LevelUpUI I { get => throw null; set => throw null; }
    public Il2CppReferenceArray<LevelUpCurEquipItem> CurHeroItems { get => throw null; set => throw null; }
    public Il2CppReferenceArray<LevelUpCurEquipItem> CurPassiveItems { get => throw null; set => throw null; }
    public int _numUpgradeChoices { get => throw null; set => throw null; }
    public LevelUpType Type { get => throw null; set => throw null; }
    public void Activate(LevelUpType t) => throw null;
    public void PopulateUpgrades() => throw null;
    public List<UpgradeChoice> _availMerges { get => throw null; set => throw null; }
    public List<HeroCombo> _availHCombos { get => throw null; set => throw null; }
}

public class LevelUpCurEquipItem : MonoBehaviour
{
    public LevelUpCurEquipItem(IntPtr pointer) : base(pointer) { }
}

namespace I2.Loc
{
    public static class LocalizationManager
    {
        public static string GetTranslation(string Term, bool FixForRTL = true, int maxLineLengthForRTL = 0, bool ignoreRTLnumbers = true,
            bool applyParameters = false, GameObject localParametersRoot = null, string overrideLanguage = null, bool allowLocalizedParameters = true) => throw null;
    }
}

public class TimeMgr : MonoBehaviour
{
    public TimeMgr(IntPtr pointer) : base(pointer) { }
    public static TimeMgr I { get => throw null; set => throw null; }
    public float _gameTime { get => throw null; set => throw null; }
    public float _timeDebt { get => throw null; set => throw null; }
    public float _physicsTime { get => throw null; set => throw null; }
    public float _gameSpeed { get => throw null; set => throw null; }
    public float _defaultFixedDeltaTime { get => throw null; set => throw null; }
    public float _gameFixedDeltaTime { get => throw null; set => throw null; }
    public float GetTime() => throw null;
    public void RunFixedUpdate(float fixedDeltaTime) => throw null;
}

public enum GameState { kPlaying, kGameOver, kLevelUp, kPaused, kPickTreasure, kFoundBlueprint, kBonusBall, kBonusPassive, kFoundEgg, kRevive, kEndingGame, kEnteringLvl, kNum }

public enum LoadMode { kNewGame, kLoad, kUndo, kResetFloor }

public enum LevelType { }

public enum CharType { }

public enum GameActionType
{
    kShoot, kAimHorizontal, kPause, kMoveHorizontal, kAimVertical, kMoveVertical, kAutofire, kIncreaseSpeed, kDecreaseSpeed, kLevelUp,
    kGrabModifier, kRotateCW, kRotateCCW, kWorkerDetails, kZoomIn, kZoomOut, kShowUpgradeable, kShowFullyUpgraded, kDismantleBuilding,
    kSpeedUpHarvest, kCancelHarvest, kOpenSidebar, kNum,
}

// Real base class is BaseMgr.
public class GameMgr : MonoBehaviour
{
    public GameMgr(IntPtr pointer) : base(pointer) { }
    public static GameMgr I { get => throw null; set => throw null; }
    public GameState CurState { get => throw null; set => throw null; }
}

public class GridMgr : MonoBehaviour
{
    public GridMgr(IntPtr pointer) : base(pointer) { }
    public static GridMgr I { get => throw null; set => throw null; }
    public Il2CppSystem.Random LvlRnd { get => throw null; set => throw null; }
    public Il2CppSystem.Random MiscRnd { get => throw null; set => throw null; }
    public void InitGrid(LoadMode loadMode) => throw null;
}

public class SaveMgr : MonoBehaviour
{
    public SaveMgr(IntPtr pointer) : base(pointer) { }
    public static SaveMgr I { get => throw null; set => throw null; }
    public void StartNewGame() => throw null;
}

public class ThreadSafeRandom : Il2CppSystem.Object
{
    public ThreadSafeRandom(IntPtr pointer) : base(pointer) { }
    public static Il2CppSystem.Random _global { get => throw null; set => throw null; }
    public static Il2CppSystem.Random _local { get => throw null; set => throw null; }
    public int Next() => throw null;
    public double NextDouble() => throw null;
    public float RandomValue() => throw null;
    public float RandomRange(float min, float max) => throw null;
    public int RandomRange(int min, int max) => throw null;
    public int RandomSign() => throw null;
}

public class InputMgr : MonoBehaviour
{
    public InputMgr(IntPtr pointer) : base(pointer) { }
    public float GetAxis(GameActionType t) => throw null;
    public bool IsBtnDown(GameActionType t) => throw null;
    public bool IsBtnHeld(GameActionType t) => throw null;
    public bool IsBtnUp(GameActionType t) => throw null;
}

public class CharBattleInst : Il2CppSystem.Object
{
    public CharBattleInst(IntPtr pointer) : base(pointer) { }
    public CharType Type { get => throw null; set => throw null; }
    public Il2CppStructArray<int> Stats { get => throw null; set => throw null; }
}

public class GridPieceInst : Il2CppSystem.Object
{
    public GridPieceInst(IntPtr pointer) : base(pointer) { }
    public float X { get => throw null; set => throw null; }
    public float Y { get => throw null; set => throw null; }
    public int CurHealth { get => throw null; set => throw null; }
}

public class PickupInst : Il2CppSystem.Object
{
    public PickupInst(IntPtr pointer) : base(pointer) { }
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
    public List<BallObj> ActiveBalls { get => throw null; set => throw null; }
    public Vector2 MousePosToAimDir(Vector3 mousePos, int idx) => throw null;
}
