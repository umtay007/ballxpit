using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BALLxPITLocalCoop;
using BALLxPITOnlineCoop.Core;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using HeroList = Il2CppSystem.Collections.Generic.List<HeroInst>;
using PassiveList = Il2CppSystem.Collections.Generic.List<PassiveInst>;

namespace BALLxPITOnlineCoop.Game;

internal enum PickKind { Any, Ball, Passive }

/// <summary>One option P2 is offered at a level-up.</summary>
internal sealed class LoadoutChoice
{
    public UpgradeInfo Info = null!;
    public bool IsBall;
    public bool IsNew;
    public HeroInst? Hero;
    public PassiveInst? Passive;
    public int FromLevel;
    public string Name = "";
    public string Color = "#ffffff";

    public string Label => IsNew
        ? $"New {(IsBall ? "ball" : "passive")}: {Name}"
        : $"{Name} Lv {FromLevel} > {FromLevel + 1}";
}

/// <summary>
/// Gives P2 its own balls, passives and level-up picks.
///
/// Local Coop already swaps P2's ball list (BattleSaveData.Heroes) in whenever the game runs P2's code,
/// but builds that list from copies of P1's balls. With this on, the add-on owns the list instead
/// (through the patched Local Coop's TakeOverPlayerTwoHeroes hook): P2 starts the run with copies of
/// P1's balls and passives, and from then on only gets what P2 picks. P2's passives
/// (BattleSaveData.Passives) and the numbers the game derives from both (UpgradeMgr's damage, crit,
/// speed, max health, ...) are swapped in and out at the same moments as the ball list.
///
/// Each time the shared level goes up, P2 gets a pick of its own: new ball, ball upgrade, new passive
/// or passive upgrade, offered on the guest's page and in the host's panel. A pick is applied with the
/// game's UpgradeMgr.ApplyUpgrade while P2's things are swapped in.
/// </summary>
internal static class PlayerTwoLoadout
{
    private const int DefaultSlots = 4;
    private const int DefaultChoices = 3;

    private static bool _installed;
    private static bool _loggedError;
    private static bool _loggedNotSwapped;

    // The run the loadout belongs to. Holding the object also keeps its address from being reused
    // by the next run's BattleSaveData, so comparing addresses is enough.
    private static BattleSaveData? _runSave;

    // P2's things. The Il2Cpp lists are what the game sees; the managed mirrors avoid native calls in hot paths.
    private static HeroList? _heroes;
    private static PassiveList? _passives;
    private static readonly List<HeroInst> HeroMirror = new();
    private static readonly List<PassiveInst> PassiveMirror = new();
    private static double _mirrorRefreshedAt;

    // Swap state while the game runs P2's code.
    private static bool _swappedIn;
    private static BattleSaveData? _swapSave;
    private static UpgradeMgr? _swapMgr;
    private static PassiveList? _playerOnePassives;
    private static StatBlock? _stats;
    private static byte[] _playerOneValues = Array.Empty<byte>();
    private static byte[] _playerTwoValues = Array.Empty<byte>();
    private static PassiveInst?[] _playerOneRefs = Array.Empty<PassiveInst?>();
    private static PassiveInst?[] _playerTwoRefs = Array.Empty<PassiveInst?>();
    private static bool _playerTwoStatsValid;
    private static bool _statsDirty;
    private static bool _recomputing;
    private static float _maxHealth;
    private static bool _wasEnabled;

    // Picks.
    private static readonly Queue<PickKind> Picks = new();
    private static int _lastLevel = int.MinValue;
    private static List<LoadoutChoice>? _offer;
    private static PickKind _offerKind;
    private static int _offerId;
    private static int _queuedChoice = -1;
    private static int _requestedOffer = -1, _requestedIndex = -1;
    private static readonly System.Random Rng = new();
    private static string _json = "";
    private static double _jsonBuiltAt;
    private static bool _jsonDirty = true;

    public static bool Enabled => _installed && OnlineConfig.SeparateLoadout.Value;
    /// <summary>P2's own numbers are in use (P2's max health comes from <see cref="MaxHealth"/>).</summary>
    public static bool StatsReady => Enabled && _playerTwoStatsValid && !_statsDirty && _maxHealth > 0;
    public static float MaxHealth => _maxHealth;
    public static int PicksWaiting => Picks.Count;
    public static IReadOnlyList<LoadoutChoice>? Offer => _offer;
    public static bool WaitingForLevelUpScreen => _queuedChoice >= 0;
    public static IReadOnlyList<HeroInst> Balls => HeroMirror;
    public static IReadOnlyList<PassiveInst> PassiveItems => PassiveMirror;

    public static void Install(Harmony harmony)
    {
        try
        {
            if (OnlineHooks.GetVersion() < 3)
            {
                Plugin.Logger.LogWarning("The installed Local Coop DLL is from an older Online Co-op build, so P2 keeps "
                    + "copies of P1's balls. Install both DLLs from this version's zip.");
                return;
            }
            InstallHooks();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("P2 can't have its own balls with this Local Coop DLL: " + ex.Message);
            return;
        }

        try
        {
            MethodInfo? calculate = AccessTools.Method(typeof(UpgradeMgr), "CalculateStats", Type.EmptyTypes);
            if (calculate != null)
                harmony.Patch(calculate, postfix: new HarmonyMethod(AccessTools.Method(typeof(PlayerTwoLoadout), nameof(CalculateStatsPostfix))));
            MethodInfo? activate = AccessTools.Method(typeof(LevelUpUI), "Activate", new[] { typeof(LevelUpType) });
            if (activate != null)
                harmony.Patch(activate, prefix: new HarmonyMethod(AccessTools.Method(typeof(PlayerTwoLoadout), nameof(LevelUpActivatePrefix))));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Couldn't watch the game's stat and level-up code; P2's stats may lag behind: " + ex.Message);
        }
        _installed = true;
        Plugin.Logger.LogInfo("P2 has its own balls, passives and level-up picks (turn off with SeparateLoadout in the config).");
    }

    // Kept separate so a Local Coop DLL without these hooks fails here, where it is caught.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallHooks()
    {
        OnlineHooks.OwnsPlayerTwoHeroes = OwnsHeroes;
        OnlineHooks.InventoryContextChanged = OnContextChanged;
    }

    /// <summary>Network thread: the guest picked choice <paramref name="index"/> of offer <paramref name="offerId"/>.</summary>
    public static void RequestPick(int offerId, int index)
    {
        Volatile.Write(ref _requestedIndex, index);
        Volatile.Write(ref _requestedOffer, offerId);
    }

    // ---- Hooks called by the patched Local Coop -----------------------------------------------

    /// <summary>
    /// PlayerTwoHeroInventory.EnsureInventoryReady / InvalidateForHeroChanges: true when the add-on
    /// supplies P2's ball list, so Local Coop doesn't rebuild it from P1's.
    /// </summary>
    private static bool OwnsHeroes()
    {
        try
        {
            if (!Enabled) return false;
            BattleSaveData save = BattleSaveData.I;
            if (save == null) return false;
            CheckRun(save);
            if (_heroes == null) StartLoadout(save);
            if (_heroes == null) return false;
            PlayerTwoHeroInventory._playerTwoHeroes = _heroes;
            PlayerTwoHeroInventory._inventoryDirty = false;
            RegisterWithLocalCoop();
            return true;
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            return false;
        }
    }

    /// <summary>End of PlayerTwoHeroInventory.Enter/Exit/RestorePlayerOne/Reset: follow its depth.</summary>
    private static void OnContextChanged()
    {
        try
        {
            bool inside = PlayerTwoHeroInventory._contextDepth > 0;
            if (inside && !_swappedIn) SwapIn();
            else if (!inside && _swappedIn) SwapOut();
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    private static void SwapIn()
    {
        if (!Enabled || _heroes == null || _passives == null) return;
        // Only when P2 is really running on the add-on's ball list.
        if (!ReferenceEquals(PlayerTwoHeroInventory._playerTwoHeroes, _heroes)) return;
        BattleSaveData save = BattleSaveData.I;
        UpgradeMgr mgr = UpgradeMgr.I;
        if (save == null || mgr == null) return;

        _playerOnePassives = save.Passives;
        save.Passives = _passives;
        _swapSave = save;
        _swapMgr = mgr;
        _swappedIn = true;

        StatBlock stats = _stats ??= StatBlock.Build(mgr);
        EnsureBuffers(stats);
        stats.Capture(mgr, _playerOneValues, _playerOneRefs);
        if (_playerTwoStatsValid)
        {
            stats.Apply(mgr, _playerTwoValues, _playerTwoRefs);
        }
        else
        {
            // Until the game has worked out P2's numbers, P2 uses P1's.
            Buffer.BlockCopy(_playerOneValues, 0, _playerTwoValues, 0, _playerOneValues.Length);
            Array.Copy(_playerOneRefs, _playerTwoRefs, _playerOneRefs.Length);
            _playerTwoStatsValid = true;
            _statsDirty = true;
        }
    }

    private static void SwapOut()
    {
        _swappedIn = false;
        BattleSaveData? save = _swapSave;
        UpgradeMgr? mgr = _swapMgr;
        PassiveList? playerOne = _playerOnePassives;
        _swapSave = null;
        _swapMgr = null;
        _playerOnePassives = null;
        try
        {
            if (save != null && playerOne != null) save.Passives = playerOne;
        }
        finally
        {
            if (mgr != null && _stats != null)
            {
                _stats.Capture(mgr, _playerTwoValues, _playerTwoRefs);
                _stats.Apply(mgr, _playerOneValues, _playerOneRefs);
            }
        }
    }

    private static void EnsureBuffers(StatBlock stats)
    {
        if (_playerOneValues.Length != stats.ValueBytes)
        {
            _playerOneValues = new byte[stats.ValueBytes];
            _playerTwoValues = new byte[stats.ValueBytes];
            _playerTwoStatsValid = false;
        }
        if (_playerOneRefs.Length != stats.RefCount)
        {
            _playerOneRefs = new PassiveInst?[stats.RefCount];
            _playerTwoRefs = new PassiveInst?[stats.RefCount];
            _playerTwoStatsValid = false;
        }
    }

    // ---- Harmony patches on the game --------------------------------------------------------

    private static void CalculateStatsPostfix(UpgradeMgr __instance)
    {
        if (_recomputing || !Enabled) return;
        try
        {
            if (PlayerTwoHeroInventory._contextDepth > 0)
            {
                // The game worked out P2's numbers itself.
                if (_swappedIn) _maxHealth = __instance.MaxHealth;
            }
            else
            {
                // P1's changed (a level-up raises the character's stats, say); P2's follow on the next frame.
                _statsDirty = true;
            }
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    private static void LevelUpActivatePrefix(LevelUpType t)
    {
        try
        {
            if (!Enabled || _heroes == null || PlayerTwoController.GetPlayerTwo() == null) return;
            if (t == LevelUpType.kBonusBall) AddPick(PickKind.Ball, "a bonus ball");
            else if (t == LevelUpType.kBonusPassive) AddPick(PickKind.Passive, "a bonus passive");
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    // ---- Game thread, every frame ------------------------------------------------------------

    public static void Update()
    {
        if (!_installed) return;
        try
        {
            bool enabled = Enabled;
            if (_wasEnabled && !enabled) Disable();
            _wasEnabled = enabled;
            if (!enabled) return;

            BattleSaveData save = BattleSaveData.I;
            if (save == null) return;
            CheckRun(save);
            Player? p2 = PlayerTwoController.GetPlayerTwo();
            bool outside = PlayerTwoHeroInventory._contextDepth == 0;

            int level = save.UpgradeLvl;
            if (_lastLevel == int.MinValue || level < _lastLevel || p2 == null || _heroes == null)
            {
                _lastLevel = level;
            }
            else if (level > _lastLevel)
            {
                for (int i = Math.Min(level - _lastLevel, 20); i > 0; i--) AddPick(PickKind.Any, "a level-up");
                _lastLevel = level;
            }

            if (p2 == null || _heroes == null || !outside) return;

            double now = HostServer.Now;
            if (now - _mirrorRefreshedAt > 0.5)
            {
                RefreshMirrors();
                RegisterWithLocalCoop();
            }
            if (_statsDirty) RecomputeStats(p2);

            int requestedOffer = Interlocked.Exchange(ref _requestedOffer, -1);
            if (requestedOffer >= 0 && requestedOffer == _offerId) _queuedChoice = Volatile.Read(ref _requestedIndex);

            if (_queuedChoice >= 0 && !IsLevelUpScreenOpen())
            {
                int index = _queuedChoice;
                _queuedChoice = -1;
                Choose(p2, index);
            }
            if (_offer == null && Picks.Count > 0) MakeOffer();
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    /// <summary>The host clicked a choice in the panel.</summary>
    public static void PickFromPanel(int index)
    {
        if (_offer == null || index < 0 || index >= _offer.Count) return;
        _queuedChoice = index;
    }

    private static void AddPick(PickKind kind, string why)
    {
        if (Picks.Count >= 30) return;
        Picks.Enqueue(kind);
        _jsonDirty = true;
        Plugin.Logger.LogInfo($"P2 gets a pick for {why} ({Picks.Count} waiting).");
    }

    private static void CheckRun(BattleSaveData save)
    {
        if (_runSave != null && _runSave.Pointer == save.Pointer) return;
        if (_swappedIn) SwapOut();
        _runSave = save;
        _heroes = null;
        _passives = null;
        HeroMirror.Clear();
        PassiveMirror.Clear();
        _playerTwoStatsValid = false;
        _statsDirty = true;
        _maxHealth = 0;
        Picks.Clear();
        _offer = null;
        _queuedChoice = -1;
        _lastLevel = int.MinValue;
        _jsonDirty = true;
    }

    /// <summary>P2 starts with copies of what P1 has right now (normally just the character's first ball).</summary>
    private static void StartLoadout(BattleSaveData save)
    {
        var heroes = new HeroList();
        HeroList? source = PlayerTwoHeroInventory._contextDepth > 0 && PlayerTwoHeroInventory._playerOneHeroes != null
            ? PlayerTwoHeroInventory._playerOneHeroes
            : save.Heroes;
        if (source != null)
        {
            for (int i = 0; i < source.Count; i++)
            {
                HeroInst original = source[i];
                if (original == null) continue;
                var copy = new HeroInst(original);
                copy.Obj = null;
                heroes.Add(copy);
            }
        }

        var passives = new PassiveList();
        PassiveList? playerOnePassives = _swappedIn ? _playerOnePassives : save.Passives;
        if (playerOnePassives != null)
        {
            for (int i = 0; i < playerOnePassives.Count; i++)
            {
                PassiveInst original = playerOnePassives[i];
                if (original != null) passives.Add(new PassiveInst(original));
            }
        }

        _heroes = heroes;
        _passives = passives;
        RefreshMirrors();
        _statsDirty = true;
        _jsonDirty = true;
        Plugin.Logger.LogInfo($"P2's loadout starts with {HeroMirror.Count} ball(s) and {PassiveMirror.Count} passive(s), copied from P1.");
    }

    private static void RefreshMirrors()
    {
        _mirrorRefreshedAt = HostServer.Now;
        HeroMirror.Clear();
        PassiveMirror.Clear();
        if (_heroes != null)
        {
            for (int i = 0; i < _heroes.Count; i++)
            {
                HeroInst hero = _heroes[i];
                if (hero != null) HeroMirror.Add(hero);
            }
        }
        if (_passives != null)
        {
            for (int i = 0; i < _passives.Count; i++)
            {
                PassiveInst passive = _passives[i];
                if (passive != null) PassiveMirror.Add(passive);
            }
        }
    }

    /// <summary>Local Coop treats a ball as P2's when its HeroInst is one of its known P2 copies.</summary>
    private static void RegisterWithLocalCoop()
    {
        var clones = PlayerTwoHeroInventory._clones;
        foreach (HeroInst hero in HeroMirror)
        {
            IntPtr pointer = hero.Pointer;
            if (pointer != IntPtr.Zero && !clones.ContainsKey(pointer)) clones[pointer] = hero;
        }
    }

    /// <summary>Has the game work out P2's numbers from P2's balls and passives.</summary>
    private static void RecomputeStats(Player p2)
    {
        BattleSaveData save = BattleSaveData.I;
        UpgradeMgr mgr = UpgradeMgr.I;
        if (save == null || mgr == null) return;
        float health = save.CurHealth;
        _recomputing = true;
        Player previous = NativePlayerContext.EnterPlayerTwoContext(p2);
        try
        {
            if (_swappedIn)
            {
                mgr.CalculateStats();
                _maxHealth = mgr.MaxHealth;
                _statsDirty = false;
            }
            else if (!_loggedNotSwapped)
            {
                _loggedNotSwapped = true;
                Plugin.Logger.LogWarning("P2's balls and passives weren't swapped in for P2's code; P2 uses P1's stats for now.");
            }
        }
        finally
        {
            NativePlayerContext.ExitContext(previous);
            save.CurHealth = health;
            _recomputing = false;
        }
    }

    private static void Disable()
    {
        if (_swappedIn) SwapOut();
        // Hand P2's ball list back to Local Coop, which rebuilds it from P1's.
        if (PlayerTwoHeroInventory._contextDepth == 0 && ReferenceEquals(PlayerTwoHeroInventory._playerTwoHeroes, _heroes))
        {
            PlayerTwoHeroInventory._playerTwoHeroes = null;
            PlayerTwoHeroInventory._inventoryDirty = true;
        }
        _offer = null;
        _queuedChoice = -1;
        _jsonDirty = true;
    }

    private static bool IsLevelUpScreenOpen()
    {
        LevelUpUI ui = LevelUpUI.I;
        if (ui == null) return false;
        var stack = OverlayUI.sOverlayStack;
        if (stack == null) return false;
        IntPtr pointer = ui.Pointer;
        for (int i = 0; i < stack.Count; i++)
        {
            OverlayUI overlay = stack[i];
            if (overlay != null && overlay.Pointer == pointer) return true;
        }
        return false;
    }

    // ---- Offers and picks -----------------------------------------------------------------

    private static void MakeOffer()
    {
        while (Picks.Count > 0)
        {
            PickKind kind = Picks.Peek();
            List<LoadoutChoice> choices = BuildChoices(kind);
            if (choices.Count > 0)
            {
                _offer = choices;
                _offerKind = kind;
                _offerId = (_offerId + 1) & 0x3fffffff;
                _jsonDirty = true;
                return;
            }
            Picks.Dequeue();
            Plugin.Logger.LogInfo("P2 has nothing left to pick for this level-up; skipped it.");
        }
    }

    private static List<LoadoutChoice> BuildChoices(PickKind kind)
    {
        var candidates = new List<LoadoutChoice>();
        LevelUpUI ui = LevelUpUI.I;
        int heroSlots = SlotCount(ui == null ? null : ui.CurHeroItems);
        int passiveSlots = SlotCount(ui == null ? null : ui.CurPassiveItems);
        HashSet<IntPtr> banished = Banished();
        InfoDB db = InfoDB.I;

        if (kind != PickKind.Passive)
        {
            foreach (HeroInst hero in HeroMirror)
            {
                if (!SafeCanUpgrade(hero)) continue;
                HeroInfo info = hero.GetInfo();
                if (info != null) candidates.Add(new LoadoutChoice { Info = info, IsBall = true, Hero = hero, FromLevel = hero.Lvl, Name = NameOf(info), Color = ColorOf(info.BallColor) });
            }
            if (HeroMirror.Count < heroSlots && db != null && db.BaseHeroes != null)
                AddNewBalls(candidates, db, banished);
        }
        if (kind != PickKind.Ball)
        {
            foreach (PassiveInst passive in PassiveMirror)
            {
                if (!SafeCanUpgrade(passive)) continue;
                PassiveInfo info = passive.GetInfo();
                if (info != null) candidates.Add(new LoadoutChoice { Info = info, Passive = passive, FromLevel = passive.Lvl, Name = NameOf(info), Color = ColorOf(info.MainColor) });
            }
            if (PassiveMirror.Count < passiveSlots && db != null && db.Passives != null)
                AddNewPassives(candidates, db, banished);
        }

        int count = DefaultChoices;
        try
        {
            if (ui != null && ui._numUpgradeChoices > 0) count = Math.Clamp(ui._numUpgradeChoices, 2, 5);
        }
        catch
        {
            // keep the default
        }
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        if (candidates.Count > count) candidates.RemoveRange(count, candidates.Count - count);
        return candidates;
    }

    private static void AddNewBalls(List<LoadoutChoice> candidates, InfoDB db, HashSet<IntPtr> banished)
    {
        var all = db.BaseHeroes;
        var strict = new List<HeroInfo>();
        var loose = new List<HeroInfo>();
        for (int i = 0; i < all.Length; i++)
        {
            HeroInfo info = all[i];
            if (info == null || banished.Contains(info.Pointer) || OwnsBall(info.Type)) continue;
            bool unlocked = SafeCall(info.IsUnlocked, false);
            if (!unlocked) continue;
            loose.Add(info);
            if (SafeCall(info.CanBeUsed, true)) strict.Add(info);
        }
        foreach (HeroInfo info in strict.Count > 0 ? strict : loose)
            candidates.Add(new LoadoutChoice { Info = info, IsBall = true, IsNew = true, Name = NameOf(info), Color = ColorOf(info.BallColor) });
    }

    private static void AddNewPassives(List<LoadoutChoice> candidates, InfoDB db, HashSet<IntPtr> banished)
    {
        var all = db.Passives;
        var strict = new List<PassiveInfo>();
        var loose = new List<PassiveInfo>();
        for (int i = 0; i < all.Length; i++)
        {
            PassiveInfo info = all[i];
            if (info == null || banished.Contains(info.Pointer) || OwnsPassive(info.Type)) continue;
            bool unlocked = SafeCall(info.IsUnlocked, false);
            if (!unlocked) continue;
            loose.Add(info);
            if (SafeCall(info.CanBeUsed, true)) strict.Add(info);
        }
        foreach (PassiveInfo info in strict.Count > 0 ? strict : loose)
            candidates.Add(new LoadoutChoice { Info = info, IsNew = true, Name = NameOf(info), Color = ColorOf(info.MainColor) });
    }

    private static bool OwnsBall(HeroType type)
    {
        foreach (HeroInst hero in HeroMirror)
        {
            if (hero.Type == type) return true;
            if (SafeCall(() => hero.HasType(type), false)) return true;
        }
        return false;
    }

    private static bool OwnsPassive(PassiveType type)
    {
        foreach (PassiveInst passive in PassiveMirror)
            if (passive.Type == type) return true;
        return false;
    }

    private static HashSet<IntPtr> Banished()
    {
        var set = new HashSet<IntPtr>();
        try
        {
            var list = BattleSaveData.I?.BanishedItems;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    UpgradeInfo info = list[i];
                    if (info != null) set.Add(info.Pointer);
                }
            }
        }
        catch
        {
            // nothing banished, then
        }
        return set;
    }

    private static void Choose(Player p2, int index)
    {
        List<LoadoutChoice>? offer = _offer;
        if (offer == null || index < 0 || index >= offer.Count) return;
        LoadoutChoice choice = offer[index];
        bool applied = Apply(p2, choice);
        if (!applied)
        {
            // Offer something fresh instead of getting stuck on a choice that doesn't apply.
            _offer = null;
            _jsonDirty = true;
            return;
        }
        if (Picks.Count > 0) Picks.Dequeue();
        _offer = null;
        _jsonDirty = true;
        Plugin.Logger.LogInfo($"P2 picked {choice.Label}.");
    }

    private static bool Apply(Player p2, LoadoutChoice choice)
    {
        BattleSaveData save = BattleSaveData.I;
        UpgradeMgr mgr = UpgradeMgr.I;
        if (save == null || mgr == null || _heroes == null || _passives == null) return false;
        int oldLevel = choice.Hero?.Lvl ?? choice.Passive?.Lvl ?? 0;
        float health = save.CurHealth;
        bool applied = false;
        _recomputing = true;
        Player previous = NativePlayerContext.EnterPlayerTwoContext(p2);
        try
        {
            if (!_swappedIn) throw new InvalidOperationException("P2's balls and passives weren't swapped in.");
            try
            {
                mgr.ApplyUpgrade(new UpgradeChoice(choice.Info, choice.IsNew));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"The game couldn't apply {choice.Label} for P2 ({ex.Message}); applying it directly.");
            }
            RefreshMirrors();
            applied = IsApplied(choice, oldLevel);
            if (!applied)
            {
                ApplyDirectly(mgr, choice);
                RefreshMirrors();
                applied = IsApplied(choice, oldLevel);
            }
            RegisterWithLocalCoop();
            mgr.CalculateStats();
            _maxHealth = mgr.MaxHealth;
            _statsDirty = false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Couldn't give P2 {choice.Label}: {ex}");
        }
        finally
        {
            NativePlayerContext.ExitContext(previous);
            save.CurHealth = health;
            _recomputing = false;
        }

        // Anything that refreshed while P2's things were swapped in (the HUD's ball icons, say) showed
        // P2's; refresh it again for P1.
        try
        {
            if (choice.IsBall) mgr.OnHeroesChanged?.Invoke();
            else mgr.OnPassivesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug("Refreshing P1's HUD after P2's pick: " + ex.Message);
        }
        if (!applied) Plugin.Logger.LogWarning($"P2's pick ({choice.Label}) didn't take; offering new choices.");
        return applied;
    }

    private static bool IsApplied(LoadoutChoice choice, int oldLevel)
    {
        if (!choice.IsNew) return (choice.Hero?.Lvl ?? choice.Passive?.Lvl ?? 0) > oldLevel;
        return choice.IsBall
            ? choice.Info is HeroInfo hero ? OwnsBall(hero.Type) : OwnsBallInfo(choice.Info)
            : choice.Info is PassiveInfo passive ? OwnsPassive(passive.Type) : OwnsPassiveInfo(choice.Info);
    }

    // Il2CppInterop hands out base-typed wrappers; compare by the native object instead.
    private static bool OwnsBallInfo(UpgradeInfo info) => OwnsBall(new HeroInfo(info.Pointer).Type);
    private static bool OwnsPassiveInfo(UpgradeInfo info) => OwnsPassive(new PassiveInfo(info.Pointer).Type);

    /// <summary>Only used if ApplyUpgrade didn't do it. Runs with P2's things swapped in.</summary>
    private static void ApplyDirectly(UpgradeMgr mgr, LoadoutChoice choice)
    {
        if (!choice.IsNew)
        {
            if (choice.Hero != null) choice.Hero.Lvl++;
            else if (choice.Passive != null) choice.Passive.Lvl++;
            return;
        }
        if (choice.IsBall) mgr.AddHero(new HeroInfo(choice.Info.Pointer).Type, 0);
        else _passives!.Add(new PassiveInst(new PassiveInfo(choice.Info.Pointer).Type));
    }

    // ---- What the guest's page and the panel show -----------------------------------------------

    /// <summary>A new host starts without the last message; build it again.</summary>
    public static void ResendJson()
    {
        _json = "";
        _jsonDirty = true;
    }

    /// <summary>The "loadout" message for the guests, or null when it hasn't changed.</summary>
    public static string? TakeJson()
    {
        double now = HostServer.Now;
        if (!_jsonDirty && now - _jsonBuiltAt < 2) return null;
        _jsonBuiltAt = now;
        _jsonDirty = false;
        string json;
        try
        {
            json = BuildJson();
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            return null;
        }
        if (json == _json) return null;
        _json = json;
        return json;
    }

    private static string BuildJson()
    {
        bool on = Enabled && _heroes != null && PlayerTwoController.GetPlayerTwo() != null;
        var sb = new StringBuilder("{\"t\":\"loadout\",\"on\":").Append(on ? "true" : "false");
        if (!on) return sb.Append('}').ToString();
        sb.Append(",\"picks\":").Append(Picks.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"balls\":[");
        for (int i = 0; i < HeroMirror.Count; i++)
        {
            HeroInst hero = HeroMirror[i];
            HeroInfo info = hero.GetInfo();
            if (i > 0) sb.Append(',');
            Item(sb, info == null ? Pretty(hero.Type.ToString()) : NameOf(info), info == null ? "#ffffff" : ColorOf(info.BallColor), hero.Lvl, !SafeCanUpgrade(hero));
        }
        sb.Append("],\"passives\":[");
        for (int i = 0; i < PassiveMirror.Count; i++)
        {
            PassiveInst passive = PassiveMirror[i];
            PassiveInfo info = passive.GetInfo();
            if (i > 0) sb.Append(',');
            Item(sb, info == null ? Pretty(passive.Type.ToString()) : NameOf(info), info == null ? "#ffffff" : ColorOf(info.MainColor), passive.Lvl, !SafeCanUpgrade(passive));
        }
        sb.Append(']');
        if (_offer != null)
        {
            sb.Append(",\"offer\":{\"id\":").Append(_offerId.ToString(CultureInfo.InvariantCulture))
              .Append(",\"kind\":\"").Append(_offerKind switch { PickKind.Ball => "ball", PickKind.Passive => "passive", _ => "any" })
              .Append("\",\"wait\":").Append(_queuedChoice >= 0 ? "true" : "false").Append(",\"choices\":[");
            for (int i = 0; i < _offer.Count; i++)
            {
                LoadoutChoice c = _offer[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(HostServer.JsonString(c.Name))
                  .Append(",\"ball\":").Append(c.IsBall ? "true" : "false")
                  .Append(",\"new\":").Append(c.IsNew ? "true" : "false")
                  .Append(",\"lvl\":").Append((c.IsNew ? 1 : c.FromLevel + 1).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"color\":").Append(HostServer.JsonString(c.Color)).Append('}');
            }
            sb.Append("]}");
        }
        return sb.Append('}').ToString();
    }

    private static void Item(StringBuilder sb, string name, string color, int level, bool max)
    {
        sb.Append("{\"name\":").Append(HostServer.JsonString(name))
          .Append(",\"color\":").Append(HostServer.JsonString(color))
          .Append(",\"lvl\":").Append(level.ToString(CultureInfo.InvariantCulture))
          .Append(",\"max\":").Append(max ? "true" : "false").Append('}');
    }

    /// <summary>"Frost (2), Bleed (1)" for the host's panel.</summary>
    public static string DescribeBalls() => Describe(HeroMirror, h => h.GetInfo(), h => h.Type.ToString(), h => h.Lvl);

    public static string DescribePassives() => Describe(PassiveMirror, p => p.GetInfo(), p => p.Type.ToString(), p => p.Lvl);

    private static string Describe<TInst, TInfo>(List<TInst> items, Func<TInst, TInfo?> info, Func<TInst, string> type, Func<TInst, int> level)
        where TInfo : UpgradeInfo
    {
        if (items.Count == 0) return "none";
        var parts = new List<string>();
        foreach (TInst item in items)
        {
            string name;
            try
            {
                TInfo? i = info(item);
                name = i == null ? Pretty(type(item)) : NameOf(i);
            }
            catch
            {
                name = "?";
            }
            parts.Add($"{name} ({level(item)})");
        }
        return string.Join(", ", parts);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static readonly Dictionary<IntPtr, string> Names = new();
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

    private static string NameOf(UpgradeInfo info)
    {
        IntPtr key = info.Pointer;
        if (Names.TryGetValue(key, out string? cached)) return cached;
        string name = "";
        try
        {
            string slug = info.GetNameSlug();
            if (!string.IsNullOrEmpty(slug)) name = I2.Loc.LocalizationManager.GetTranslation(slug) ?? "";
        }
        catch
        {
            // fall back below
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                name = info.Name ?? "";
            }
            catch
            {
                name = "";
            }
        }
        name = Tags.Replace(name, "").Trim();
        if (name.Length == 0)
        {
            try
            {
                name = info.GetUpgradeType() == UpgradeType.kHero
                    ? Pretty(new HeroInfo(key).Type.ToString())
                    : Pretty(new PassiveInfo(key).Type.ToString());
            }
            catch
            {
                name = "?";
            }
        }
        if (name.Length > 40) name = name.Substring(0, 40);
        Names[key] = name;
        return name;
    }

    /// <summary>"kFreezeRay" -> "Freeze Ray".</summary>
    private static string Pretty(string enumName)
    {
        if (enumName.Length > 1 && enumName[0] == 'k' && char.IsUpper(enumName[1])) enumName = enumName.Substring(1);
        var sb = new StringBuilder();
        for (int i = 0; i < enumName.Length; i++)
        {
            if (i > 0 && char.IsUpper(enumName[i]) && !char.IsUpper(enumName[i - 1])) sb.Append(' ');
            sb.Append(enumName[i]);
        }
        return sb.ToString();
    }

    private static string ColorOf(Color c)
    {
        static int Byte(float v) => (int)Math.Round(Math.Clamp(float.IsFinite(v) ? v : 1f, 0f, 1f) * 255f);
        return "#" + Byte(c.r).ToString("x2") + Byte(c.g).ToString("x2") + Byte(c.b).ToString("x2");
    }

    private static int SlotCount(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LevelUpCurEquipItem>? items)
    {
        try
        {
            if (items != null && items.Length > 0) return items.Length;
        }
        catch
        {
            // use the default
        }
        return DefaultSlots;
    }

    private static bool SafeCanUpgrade(HeroInst hero) => SafeCall(hero.CanUpgrade, false);
    private static bool SafeCanUpgrade(PassiveInst passive) => SafeCall(passive.CanUpgrade, false);

    private static bool SafeCall(Func<bool> call, bool fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }

    private static void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogError("P2 loadout error (logged once): " + ex);
    }

    /// <summary>
    /// UpgradeMgr's own number fields (damage, crit, speed, max health, ...) and its cached PassiveInst
    /// references, found through IL2CPP's field list and copied as raw memory: fast enough to swap
    /// every time the game switches between P1's and P2's code. TgtXP stays shared, as XP is.
    /// </summary>
    private sealed unsafe class StatBlock
    {
        private static readonly HashSet<string> Shared = new(StringComparer.Ordinal) { "TgtXP" };

        private readonly (int Offset, int Size)[] _ranges;
        private readonly int[] _refOffsets;

        public int ValueBytes { get; }
        public int RefCount => _refOffsets.Length;

        private StatBlock((int, int)[] ranges, int[] refOffsets, int valueBytes)
        {
            _ranges = ranges;
            _refOffsets = refOffsets;
            ValueBytes = valueBytes;
        }

        public static StatBlock Build(UpgradeMgr mgr)
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class(mgr.Pointer);
            while (klass != IntPtr.Zero && IL2CPP.il2cpp_class_get_name_(klass) != "UpgradeMgr")
                klass = IL2CPP.il2cpp_class_get_parent(klass);
            if (klass == IntPtr.Zero) throw new InvalidOperationException("UpgradeMgr's class wasn't found.");

            var fields = new List<(int Offset, int Size)>();
            var refs = new List<int>();
            IntPtr iter = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
            {
                int flags = IL2CPP.il2cpp_field_get_flags(field);
                if ((flags & 0x10) != 0 || (flags & 0x40) != 0) continue; // static, const
                string name = IL2CPP.il2cpp_field_get_name_(field) ?? "";
                if (Shared.Contains(name)) continue;
                IntPtr type = IL2CPP.il2cpp_field_get_type(field);
                int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                int kind = IL2CPP.il2cpp_type_get_type(type);
                int size = kind switch
                {
                    0x02 or 0x04 or 0x05 => 1,            // bool, sbyte, byte
                    0x03 or 0x06 or 0x07 => 2,            // char, short, ushort
                    0x08 or 0x09 or 0x0c => 4,            // int, uint, float
                    0x0a or 0x0b or 0x0d => 8,            // long, ulong, double
                    _ => 0,
                };
                if (size > 0 && offset > 0)
                {
                    fields.Add((offset, size));
                }
                else if (kind == 0x12 && offset > 0)
                {
                    IntPtr fieldClass = IL2CPP.il2cpp_class_from_type(type);
                    if (fieldClass != IntPtr.Zero && IL2CPP.il2cpp_class_get_name_(fieldClass) == "PassiveInst") refs.Add(offset);
                }
            }
            fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var ranges = new List<(int Offset, int Size)>();
            int total = 0;
            foreach (var (offset, size) in fields)
            {
                total += size;
                if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Size == offset)
                    ranges[^1] = (ranges[^1].Offset, ranges[^1].Size + size);
                else
                    ranges.Add((offset, size));
            }
            Plugin.Logger.LogInfo($"P2's own stats: {fields.Count} UpgradeMgr number fields ({total} bytes in {ranges.Count} run(s)) and {refs.Count} passive reference(s).");
            return new StatBlock(ranges.ToArray(), refs.ToArray(), total);
        }

        public void Capture(UpgradeMgr mgr, byte[] values, PassiveInst?[] refs)
        {
            byte* obj = (byte*)mgr.Pointer;
            fixed (byte* dst = values)
            {
                int at = 0;
                foreach (var (offset, size) in _ranges)
                {
                    Buffer.MemoryCopy(obj + offset, dst + at, size, size);
                    at += size;
                }
            }
            for (int i = 0; i < _refOffsets.Length; i++)
            {
                IntPtr value = *(IntPtr*)(obj + _refOffsets[i]);
                PassiveInst? held = refs[i];
                if ((held == null ? IntPtr.Zero : held.Pointer) != value)
                    refs[i] = value == IntPtr.Zero ? null : new PassiveInst(value);
            }
        }

        public void Apply(UpgradeMgr mgr, byte[] values, PassiveInst?[] refs)
        {
            IntPtr pointer = mgr.Pointer;
            byte* obj = (byte*)pointer;
            fixed (byte* src = values)
            {
                int at = 0;
                foreach (var (offset, size) in _ranges)
                {
                    Buffer.MemoryCopy(src + at, obj + offset, size, size);
                    at += size;
                }
            }
            for (int i = 0; i < _refOffsets.Length; i++)
            {
                IntPtr wanted = refs[i]?.Pointer ?? IntPtr.Zero;
                IntPtr* slot = (IntPtr*)(obj + _refOffsets[i]);
                if (*slot != wanted) IL2CPP.il2cpp_gc_wbarrier_set_field(pointer, (IntPtr)slot, wanted);
            }
        }
    }
}
