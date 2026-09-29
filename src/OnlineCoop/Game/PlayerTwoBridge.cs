using System;
using System.Runtime.CompilerServices;
using BALLxPITLocalCoop;
using BALLxPITOnlineCoop.Core;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

/// <summary>
/// Feeds the online guest's input into Local Coop's P2, through the two hooks the patched
/// BALLxPITLocalCoop.dll exposes:
///   * OnlineHooks.RemoteKeyHeld: P2's Shoot key counts as held while the guest holds shoot, so the
///     mod's own shooting logic (intro, recall and game-over checks included) runs unchanged.
///   * OnlineHooks.BeforeApplyInput: runs inside PlayerTwoController.UpdatePlayerTwoInput, right before
///     the mod applies P2's movement and aim; we put the guest's stick and aim into its fields there.
/// Aim stays in the mod's "keyboard" mode, where the mod itself answers the game's aim queries for P2
/// with the direction we set.
/// </summary>
internal static class PlayerTwoBridge
{
    private const double StaleInputSeconds = 1.0;

    private static HostServer? _server;
    private static int _snapshotFrame = -1;
    private static bool _guestActive;
    private static GuestInput _input;
    private static bool _loggedHookError;
    private static bool _loggedAimError;

    /// <summary>False when the installed BALLxPITLocalCoop.dll is the original one without the hooks.</summary>
    public static bool HooksInstalled { get; private set; }

    public static void Install()
    {
        try
        {
            InstallHooks();
            HooksInstalled = true;
        }
        catch (Exception ex)
        {
            HooksInstalled = false;
            Plugin.Logger.LogWarning("The installed BALLxPITLocalCoop.dll has no online hooks, so guests can watch but not control P2. "
                + "Replace it with the BALLxPITLocalCoop.dll that comes with BALLxPIT Online Coop. (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>True when the installed Local Coop DLL can also stop P2 shooting (hooks version 2).</summary>
    public static bool ShotHookInstalled { get; private set; }

    // Kept separate so a missing OnlineHooks type fails here, where it is caught.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallHooks()
    {
        int version = OnlineHooks.GetVersion();
        if (version < 1) throw new InvalidOperationException("hook version");
        OnlineHooks.RemoteKeyHeld = RemoteKeyHeld;
        OnlineHooks.BeforeApplyInput = BeforeApplyInput;
        if (version >= 2)
        {
            InstallShotHook();
            ShotHookInstalled = true;
        }
        else
        {
            Plugin.Logger.LogWarning("The installed BALLxPITLocalCoop.dll is an older online build; a knocked-out P2 can still shoot. Install the one from this download.");
        }
    }

    // Only compiled when the DLL has the version 2 field.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallShotHook()
    {
        OnlineHooks.AllowPlayerTwoShot = () => !PlayerTwoHealth.IsDowned;
    }

    public static void SetServer(HostServer? server)
    {
        _server = server;
        _snapshotFrame = -1;
        _guestActive = false;
        _input = default;
    }

    public static bool IsPlayerTwoActive()
    {
        try
        {
            return PlayerTwoController.IsPlayerTwoActive();
        }
        catch
        {
            return false;
        }
    }

    public static HostStatus ReadStatus()
    {
        var status = new HostStatus();
        try
        {
            PlayerTwoController? controller = PlayerTwoController._instance;
            status.PlayerTwoActive = controller != null && controller._playerTwo != null;
            if (controller != null) status.AutoShoot = controller._playerTwoAutoShootEnabled;
            status.AutoShootByCharacter = status.PlayerTwoActive && CharacterRuntimeInfo.AlwaysShoots;
            if (status.PlayerTwoActive && PlayerTwoHealth.Enabled && PlayerTwoHealth.HasPlayerTwo)
            {
                status.Health = PlayerTwoHealth.Health;
                status.MaxHealth = PlayerTwoHealth.MaxHealth;
                status.DownedSeconds = PlayerTwoHealth.DownedSecondsLeft;
            }
            else
            {
                status.Health = -1;
            }
        }
        catch
        {
        }
        return status;
    }

    /// <summary>Guest asked to flip auto-shoot, as if P2 pressed the ToggleAutoShoot key.</summary>
    public static void ToggleAutoShoot()
    {
        try
        {
            PlayerTwoController? controller = PlayerTwoController._instance;
            if (controller != null && controller._playerTwo != null)
                controller.TogglePlayerTwoAutoShoot("online guest");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Could not toggle P2 auto-shoot: " + ex.Message);
        }
    }

    /// <summary>Takes one snapshot of the guest's input per frame, dropping it if it went stale.</summary>
    private static void RefreshSnapshot()
    {
        int frame = Time.frameCount;
        if (frame == _snapshotFrame) return;
        _snapshotFrame = frame;
        HostServer? server = _server;
        if (server == null || !server.TryGetPlayerInput(out GuestInput input))
        {
            _guestActive = false;
            return;
        }
        _guestActive = true;
        if (HostServer.Now - input.ReceivedAt > StaleInputSeconds)
        {
            // Connection hiccup: let go of the stick and trigger but keep the aim.
            input.MoveX = 0;
            input.MoveY = 0;
            input.Shoot = false;
        }
        _input = input;
    }

    private static bool RemoteKeyHeld(KeyCode key)
    {
        try
        {
            RefreshSnapshot();
            return _guestActive && _input.Shoot && key == PlayerTwoControls.Shoot;
        }
        catch (Exception ex)
        {
            LogHookError(ex);
            return false;
        }
    }

    private static void BeforeApplyInput(PlayerTwoController controller)
    {
        try
        {
            if (controller == null || controller._playerTwo == null) return;
            if (PlayerTwoHealth.IsDowned)
            {
                controller._playerTwoMoveDir = new Vector2(0f, 0f); // knocked out: stay put
                return;
            }
            RefreshSnapshot();
            if (!_guestActive) return;

            float mx = _input.MoveX, my = _input.MoveY;
            float length = MathF.Sqrt(mx * mx + my * my);
            if (length > 1)
            {
                mx /= length;
                my /= length;
                length = 1;
            }
            Vector2 local = controller._playerTwoMoveDir;
            // Whoever pushes harder wins, so the host can still nudge P2 with the local P2 keys.
            if (length > 0.05f || local.x * local.x + local.y * local.y < 0.0025f)
            {
                if (length >= MathF.Sqrt(local.x * local.x + local.y * local.y))
                    controller._playerTwoMoveDir = new Vector2(mx, my);
            }

            if (_input.AimMode != GuestAimMode.None && TryComputeAim(controller, out Vector2 aim))
            {
                controller._playerTwoAimMode = PlayerTwoController.PlayerTwoAimMode.Keyboard;
                controller._playerTwoAimDir = aim;
            }
        }
        catch (Exception ex)
        {
            LogHookError(ex);
        }
    }

    /// <summary>
    /// Turns the guest's pointer (a spot on the streamed picture) or stick direction into P2's aim,
    /// using the game's own BallMgr.MousePosToAimDir so the aim arc and origin match a real mouse.
    /// </summary>
    private static bool TryComputeAim(PlayerTwoController controller, out Vector2 aim)
    {
        aim = default;
        Player p2 = controller._playerTwo;
        Vector3 origin = ShootOrigin(controller, p2);
        Vector3 target;
        if (_input.AimMode == GuestAimMode.Pointer)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;
            float u = Math.Clamp(_input.AimX, 0f, 1f), v = Math.Clamp(_input.AimY, 0f, 1f);
            float sx = u * Screen.width, sy = (1f - v) * Screen.height;
            if (!TryScreenToWorld(cam, origin, sx, sy, out target)) return false;
        }
        else
        {
            float dx = _input.AimX, dy = _input.AimY;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return false;
            target = new Vector3(origin.x + dx / len * 50f, origin.y + dy / len * 50f, origin.z);
        }

        Vector2 dir = default;
        BallMgr manager = BallMgr.I;
        if (manager != null)
        {
            // With P2 in keyboard mode the mod would answer this call with P2's current aim; switch
            // to mouse mode for the call so the game computes a fresh direction from the target.
            PlayerTwoController.PlayerTwoAimMode savedMode = controller._playerTwoAimMode;
            controller._playerTwoAimMode = PlayerTwoController.PlayerTwoAimMode.Mouse;
            Player? previous = NativePlayerContext.EnterPlayerTwoContext(p2);
            try
            {
                dir = manager.MousePosToAimDir(target, 0);
            }
            catch (Exception ex)
            {
                if (!_loggedAimError)
                {
                    _loggedAimError = true;
                    Plugin.Logger.LogWarning("BallMgr.MousePosToAimDir failed for the online guest; using a plain direction. " + ex.Message);
                }
            }
            finally
            {
                NativePlayerContext.ExitContext(previous);
                controller._playerTwoAimMode = savedMode;
            }
        }

        if (dir.x * dir.x + dir.y * dir.y <= 0.0001f)
        {
            // Fallback: straight line from P2, kept inside the arc the mod uses for keyboard aim.
            float dx = target.x - origin.x, dy = target.y - origin.y;
            if (dx * dx + dy * dy <= 0.0001f) return false;
            controller.GetNativeAimArcDegrees(out float min, out float max);
            float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;
            if (angle < 0) angle += 360f;
            if (angle > 270f) angle = min; // pointing below P2 on the right
            angle = Math.Clamp(angle, min, max) * MathF.PI / 180f;
            dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        float length = MathF.Sqrt(dir.x * dir.x + dir.y * dir.y);
        aim = new Vector2(dir.x / length, dir.y / length);
        return true;
    }

    private static bool _screenToWorldMissing;

    /// <summary>Screen pixel to a world point on P2's plane.</summary>
    private static bool TryScreenToWorld(Camera cam, Vector3 origin, float sx, float sy, out Vector3 world)
    {
        if (!_screenToWorldMissing)
        {
            try
            {
                world = ScreenToWorldPoint(cam, origin, sx, sy);
                return true;
            }
            catch (Exception ex)
            {
                // Probably stripped from this build; the fallback below only needs WorldToScreenPoint,
                // which Local Coop itself relies on.
                _screenToWorldMissing = true;
                Plugin.Logger.LogInfo($"Camera.ScreenToWorldPoint isn't usable here ({ex.GetType().Name}); aiming through WorldToScreenPoint instead.");
            }
        }
        return TryInvertWorldToScreen(cam, origin, sx, sy, out world);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector3 ScreenToWorldPoint(Camera cam, Vector3 origin, float sx, float sy)
    {
        float depth = MathF.Abs(origin.z - cam.transform.position.z);
        if (depth < 0.01f) depth = 10f;
        return cam.ScreenToWorldPoint(new Vector3(sx, sy, depth));
    }

    /// <summary>
    /// Projects P2's position and one world unit along x and y to the screen, then solves the 2x2
    /// system for the screen point. Exact for the orthographic camera a 2D game uses.
    /// </summary>
    private static bool TryInvertWorldToScreen(Camera cam, Vector3 origin, float sx, float sy, out Vector3 world)
    {
        world = origin;
        Vector3 s0 = cam.WorldToScreenPoint(origin);
        Vector3 s1 = cam.WorldToScreenPoint(new Vector3(origin.x + 1f, origin.y, origin.z));
        Vector3 s2 = cam.WorldToScreenPoint(new Vector3(origin.x, origin.y + 1f, origin.z));
        float ax = s1.x - s0.x, ay = s1.y - s0.y, bx = s2.x - s0.x, by = s2.y - s0.y;
        float det = ax * by - bx * ay;
        if (MathF.Abs(det) < 1e-6f) return false;
        float dx = sx - s0.x, dy = sy - s0.y;
        float a = (dx * by - bx * dy) / det;
        float b = (ax * dy - dx * ay) / det;
        world = new Vector3(origin.x + a, origin.y + b, origin.z);
        return true;
    }

    private static Vector3 ShootOrigin(PlayerTwoController controller, Player p2)
    {
        PlayerCharController character = controller._playerTwoController;
        if (character != null)
        {
            Transform shoot = character.ShootXfm;
            if (shoot != null) return shoot.position;
        }
        return NativePlayerContext.GetPlayerPosition(p2);
    }

    private static void LogHookError(Exception ex)
    {
        if (_loggedHookError) return;
        _loggedHookError = true;
        Plugin.Logger.LogError("Online P2 input failed (logged once): " + ex);
    }
}
