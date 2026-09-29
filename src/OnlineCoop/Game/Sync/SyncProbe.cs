using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BALLxPITLocalCoop;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game.Sync;

/// <summary>
/// Takes a snapshot of the run once per frame: time, both players, health and progress, every enemy,
/// every ball, pickups and the game's random number generators. Two PCs playing the same run in step
/// produce the same snapshots, number for number.
/// </summary>
internal static class SyncProbe
{
    public static readonly string[] Names =
    {
        "game time", "physics time", "fixed steps", "game speed",
        "your x", "your y", "your aim x", "your aim y",
        "health", "XP", "level", "kills", "turn",
        "enemies", "enemy x total", "enemy y total", "enemy health total",
        "balls", "ball x total", "ball y total",
        "pickups",
        "level random numbers", "misc random numbers", "shared random numbers",
        "P2 x", "P2 y",
    };

    /// <summary>What each value belongs to, for the report.</summary>
    public static readonly string[] Groups =
    {
        "time", "time", "time", "time",
        "your character", "your character", "your aim", "your aim",
        "health and progress", "health and progress", "health and progress", "health and progress", "health and progress",
        "enemies", "enemies", "enemies", "enemies",
        "balls", "balls", "balls",
        "pickups",
        "random numbers", "random numbers", "random numbers",
        "P2", "P2",
    };

    /// <summary>
    /// Allowed difference per value. Times are counted from the start of the run while the game keeps
    /// its own clock since it was launched, so they get a little leeway; everything else must match exactly.
    /// </summary>
    public static readonly double[] Tolerance = Names.Select(n => n.EndsWith(" time", StringComparison.Ordinal) ? 0.002 : 0.0).ToArray();

    public static double[] Sample(int fixedSteps, double gameTimeAtStart, double physicsTimeAtStart)
    {
        var v = new double[Names.Length];
        TimeMgr time = TimeMgr.I;
        if (time != null)
        {
            v[0] = time._gameTime - gameTimeAtStart;
            v[1] = time._physicsTime - physicsTimeAtStart;
            v[3] = time._gameSpeed;
        }
        v[2] = fixedSteps;

        Player? p1 = Player.I;
        if (p1 != null && NativePlayerContext.IsPlayerTwo(p1)) p1 = NativePlayerContext.OriginalPlayer;
        if (p1 != null)
        {
            Vector3 pos = NativePlayerContext.GetPlayerPosition(p1);
            v[4] = pos.x;
            v[5] = pos.y;
            Vector2 aim = p1._lastAimDir;
            v[6] = aim.x;
            v[7] = aim.y;
        }

        BattleSaveData save = BattleSaveData.I;
        if (save != null)
        {
            v[8] = save.CurHealth;
            v[9] = save.CurXP;
            v[10] = save.UpgradeLvl;
            v[11] = save.NumKills;
            v[12] = save.CurTurn;
            var pieces = save.Pieces;
            if (pieces != null)
            {
                int count = pieces.Count;
                v[13] = count;
                for (int i = 0; i < count; i++)
                {
                    GridPieceInst piece = pieces[i];
                    if (piece == null) continue;
                    v[14] += piece.X;
                    v[15] += piece.Y;
                    v[16] += piece.CurHealth;
                }
            }
            var pickups = save.Pickups;
            v[20] = pickups?.Count ?? 0;
        }

        BallMgr balls = BallMgr.I;
        var active = balls?.ActiveBalls;
        if (active != null)
        {
            int count = active.Count;
            v[17] = count;
            for (int i = 0; i < count; i++)
            {
                BallObj ball = active[i];
                if (ball == null) continue;
                Vector3 pos = ball.transform.position;
                v[18] += pos.x;
                v[19] += pos.y;
            }
        }

        GridMgr grid = GridMgr.I;
        if (grid != null)
        {
            v[21] = NativeFields.RandomState(grid.LvlRnd?.Pointer ?? IntPtr.Zero);
            v[22] = NativeFields.RandomState(grid.MiscRnd?.Pointer ?? IntPtr.Zero);
        }
        v[23] = NativeFields.RandomState(ThreadSafeRandom._global?.Pointer ?? IntPtr.Zero);

        Player? p2 = PlayerTwoController.GetPlayerTwo();
        if (p2 != null)
        {
            Vector3 pos = NativePlayerContext.GetPlayerPosition(p2);
            v[24] = pos.x;
            v[25] = pos.y;
        }
        return v;
    }

    /// <summary>What the run starts from: character, level, stats (from both saves' upgrades), balls, time settings.</summary>
    public static List<(string Name, string Value)> Setup()
    {
        var setup = new List<(string, string)>();
        BattleSaveData save = BattleSaveData.I;
        if (save != null)
        {
            CharBattleInst ch = save.CurChar;
            if (ch != null)
            {
                setup.Add(("character", ch.Type.ToString()));
                var stats = ch.Stats;
                if (stats != null)
                    setup.Add(("character stats", string.Join(" ", Enumerable.Range(0, stats.Length).Select(i => stats[i].ToString(CultureInfo.InvariantCulture)))));
            }
            setup.Add(("level", save.CurLevel.ToString()));
            setup.Add(("difficulty", save.CurDifficulty.ToString(CultureInfo.InvariantCulture)));
            setup.Add(("new game plus", save.CurNGPlusLvl.ToString(CultureInfo.InvariantCulture)));
            setup.Add(("balls", Describe(save.Heroes, h => $"{h.Type}:{h.Lvl}")));
            setup.Add(("passives", Describe(save.Passives, p => $"{p.Type}:{p.Lvl}")));
        }
        TimeMgr time = TimeMgr.I;
        if (time != null)
        {
            setup.Add(("game speed", time._gameSpeed.ToString("R", CultureInfo.InvariantCulture)));
            setup.Add(("fixed step", time._gameFixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)));
            setup.Add(("default fixed step", time._defaultFixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)));
        }
        UpgradeMgr mgr = UpgradeMgr.I;
        if (mgr != null)
        {
            foreach (var (name, value) in NativeFields.DescribeNumbers(mgr.Pointer, "UpgradeMgr"))
                setup.Add(("stat " + name, value));
        }
        setup.Add(("P2 present", PlayerTwoController.GetPlayerTwo() != null ? "yes" : "no"));
        return setup;
    }

    private static string Describe<T>(Il2CppSystem.Collections.Generic.List<T>? list, Func<T, string> describe) where T : Il2CppSystem.Object
    {
        if (list == null) return "";
        var parts = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            T item = list[i];
            if (item != null) parts.Add(describe(item));
        }
        return string.Join(" ", parts);
    }
}
