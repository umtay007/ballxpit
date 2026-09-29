using System;
using System.Reflection;
using BALLxPITLocalCoop;
using HarmonyLib;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

/// <summary>
/// Gives P2 its own health. The game keeps one health value (BattleSaveData.CurHealth) and
/// Player.Damage / Player.Heal change it; while those run for P2, P2's own value is swapped in and
/// out around the call. The patches run first and last so Local Coop's own guard (which stops P2
/// from starting the game-over sequence) sees P2's health.
///
/// At zero P2 is knocked out instead of dying: it can't move or shoot and takes no damage, then gets
/// back up after a while with part of its health. P1 reaching zero still ends the run, as in the game.
/// Heals that reach P1 heal P2 by the same amount, since hearts may only be collectable by P1.
/// </summary>
internal static class PlayerTwoHealth
{
    private sealed class Swap
    {
        public float PlayerOneHealth;
        public bool ForPlayerTwo;
    }

    private static IntPtr _playerTwo;
    private static float _health;
    private static float _max;
    private static bool _downed;
    private static float _downedUntil;
    private static bool _patched;
    private static bool _loggedError;

    public static bool Enabled => _patched && OnlineConfig.SeparateHealth.Value;
    public static bool HasPlayerTwo => _playerTwo != IntPtr.Zero;
    public static bool IsDowned => Enabled && HasPlayerTwo && _downed;
    public static float Health => _health;
    public static float MaxHealth => _max;
    public static float DownedSecondsLeft => IsDowned ? Math.Max(0f, _downedUntil - Now()) : 0f;

    public static void Install(Harmony harmony)
    {
        try
        {
            MethodInfo? damage = AccessTools.Method(typeof(Player), "Damage", new[] { typeof(float), typeof(PieceDmgType) });
            MethodInfo? heal = AccessTools.Method(typeof(Player), "Heal", new[] { typeof(float) });
            if (damage == null || heal == null)
            {
                Plugin.Logger.LogWarning("Player.Damage/Heal not found; P2 keeps sharing health with P1.");
                return;
            }
            harmony.Patch(damage,
                prefix: Hook(nameof(DamagePrefix), Priority.First),
                postfix: Hook(nameof(DamagePostfix), Priority.Last));
            harmony.Patch(heal,
                prefix: Hook(nameof(HealPrefix), Priority.First),
                postfix: Hook(nameof(HealPostfix), Priority.Last));
            _patched = true;
            Plugin.Logger.LogInfo("P2 has its own health (turn off with SeparateHealth in the config).");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Could not give P2 separate health; it keeps sharing P1's: " + ex);
        }
    }

    private static HarmonyMethod Hook(string name, int priority) =>
        new(AccessTools.Method(typeof(PlayerTwoHealth), name)) { priority = priority };

    /// <summary>Game thread, every frame: follows P2 appearing, max-health changes and getting back up.</summary>
    public static void Update()
    {
        if (!_patched) return;
        try
        {
            Player? p2 = PlayerTwoController.GetPlayerTwo();
            IntPtr pointer = p2 == null ? IntPtr.Zero : p2.Pointer;
            float max = CurrentMaxHealth();
            if (pointer != _playerTwo)
            {
                _playerTwo = pointer;
                _downed = false;
                if (pointer != IntPtr.Zero)
                {
                    _max = max > 0 ? max : 100f;
                    _health = _max;
                    Plugin.Logger.LogInfo($"P2 starts with {_health:0}/{_max:0} health.");
                }
                return;
            }
            if (pointer == IntPtr.Zero) return;

            if (max > 0 && Math.Abs(max - _max) > 0.01f)
            {
                // Max health went up (an Endurance level, say): P2 gains the same amount, like P1.
                if (max > _max && !_downed) _health += max - _max;
                _max = max;
                _health = Math.Min(_health, _max);
            }
            if (_downed && Now() >= _downedUntil)
            {
                _downed = false;
                _health = Math.Max(1f, _max * Math.Clamp(OnlineConfig.ReviveHealthPercent.Value, 1, 100) / 100f);
                Plugin.Logger.LogInfo($"P2 is back up with {_health:0}/{_max:0} health.");
            }
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    // ---- Player.Damage --------------------------------------------------------------------

    private static bool DamagePrefix(Player __instance, ref EnemyAttackResult __result, out Swap? __state)
    {
        __state = null;
        try
        {
            if (!Enabled || !IsPlayerTwo(__instance)) return true;
            if (_downed)
            {
                __result = EnemyAttackResult.kInvulnerable;
                return false;
            }
            BattleSaveData save = BattleSaveData.I;
            if (save == null) return true;
            __state = new Swap { PlayerOneHealth = save.CurHealth, ForPlayerTwo = true };
            save.CurHealth = _health;
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
        return true;
    }

    private static void DamagePostfix(Swap? __state)
    {
        if (__state == null) return;
        try
        {
            BattleSaveData save = BattleSaveData.I;
            if (save == null) return;
            _health = Math.Max(0f, save.CurHealth);
            save.CurHealth = __state.PlayerOneHealth;
            if (_health <= 0f && !_downed)
            {
                _downed = true;
                _downedUntil = Now() + Math.Clamp(OnlineConfig.DownedSeconds.Value, 1, 600);
                Plugin.Logger.LogInfo($"P2 is knocked out for {OnlineConfig.DownedSeconds.Value} s.");
            }
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    // ---- Player.Heal ------------------------------------------------------------------------

    private static bool HealPrefix(Player __instance, out Swap? __state)
    {
        __state = null;
        try
        {
            if (!Enabled || !HasPlayerTwo) return true;
            BattleSaveData save = BattleSaveData.I;
            if (save == null) return true;
            if (IsPlayerTwo(__instance))
            {
                if (_downed) return false; // no healing while knocked out
                __state = new Swap { PlayerOneHealth = save.CurHealth, ForPlayerTwo = true };
                save.CurHealth = _health;
            }
            else
            {
                __state = new Swap { PlayerOneHealth = save.CurHealth, ForPlayerTwo = false };
            }
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
        return true;
    }

    private static void HealPostfix(Swap? __state)
    {
        if (__state == null) return;
        try
        {
            BattleSaveData save = BattleSaveData.I;
            if (save == null) return;
            if (__state.ForPlayerTwo)
            {
                _health = Math.Max(0f, save.CurHealth);
                save.CurHealth = __state.PlayerOneHealth;
            }
            else if (OnlineConfig.SharedHealing.Value && !_downed)
            {
                float healed = save.CurHealth - __state.PlayerOneHealth;
                if (healed > 0f) _health = Math.Min(_max > 0 ? _max : float.MaxValue, _health + healed);
            }
        }
        catch (Exception ex)
        {
            LogOnce(ex);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static bool IsPlayerTwo(Player player) =>
        player != null && _playerTwo != IntPtr.Zero && player.Pointer == _playerTwo;

    private static float CurrentMaxHealth()
    {
        UpgradeMgr upgrades = UpgradeMgr.I;
        return upgrades == null ? 0f : upgrades.MaxHealth;
    }

    private static bool _timeMgrBroken;

    /// <summary>The game's own clock (stops while paused), or real time if that isn't available.</summary>
    private static float Now()
    {
        if (!_timeMgrBroken)
        {
            try
            {
                TimeMgr time = TimeMgr.I;
                if (time != null) return time.GetTime();
            }
            catch
            {
                _timeMgrBroken = true;
            }
        }
        return Time.unscaledTime;
    }

    private static void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Plugin.Logger.LogError("P2 health error (logged once): " + ex);
    }

    /// <summary>Draws P2's health (or knock-out countdown) above P2. OnGUI only.</summary>
    public static void DrawLabel()
    {
        if (!Enabled || !HasPlayerTwo) return;
        Player? p2 = PlayerTwoController.GetPlayerTwo();
        Camera cam = Camera.main;
        if (p2 == null || cam == null) return;
        Vector3 screen = cam.WorldToScreenPoint(NativePlayerContext.GetPlayerPosition(p2));
        if (screen.z < 0) return;
        string text = _downed ? $"P2 DOWN {Math.Ceiling(DownedSecondsLeft):0}s" : $"P2 {Math.Ceiling(_health):0}/{_max:0}";
        GUI.Button(new Rect(screen.x - 60f, Screen.height - screen.y - 90f, 120f, 22f), text);
    }
}
