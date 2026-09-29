using System;
using System.Collections.Generic;
using System.IO;
using BALLxPITOnlineCoop.Core;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

/// <summary>Hosting lifecycle, per-frame glue and the host's on-screen panel.</summary>
internal static class OnlineController
{
    private const float PanelMargin = 16f;
    private const float RowHeight = 28f;
    private const float RowGap = 4f;

    private static OnlineHost? _host;
    private static bool _panelVisible;
    private static string? _startError;
    private static string _copiedUrl = "";
    private static float _copiedUntil;
    private static bool _copyFailed;
    private static readonly HashSet<string> LoggedLinks = new();
    private static bool _loggedGuiError;
    private static bool _loggedCaptureLoopError;
    private static bool _loggedUpdateError;
    private static bool _loggedKeyError;
    private static bool _loggedFirstGui;

    private static Camera? _tapCamera;
    private static float _nextTapCheck;
    private static bool _loggedNoListener;
    private static bool _audioTapBroken;

    private static float _nextStatus;

    public static bool IsHosting => _host?.IsHosting == true;

    public static void Start(MonoBehaviour owner)
    {
        try
        {
            owner.StartCoroutine(FrameGrabber.EndOfFrameLoop(OnEndOfFrame));
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Could not start the end-of-frame capture loop; guests won't get a picture: " + ex);
        }
        Plugin.Logger.LogInfo("Online co-op is running.");
        if (OnlineConfig.HostOnStartup.Value) StartHosting();
    }

    public static void Update()
    {
        Plugin.FlushLogs();
        if (PanelKeyPressed())
        {
            _panelVisible = !_panelVisible;
            Plugin.Logger.LogInfo(_panelVisible ? "Online co-op panel opened." : "Online co-op panel closed.");
        }

        PlayerTwoLoadout.Update();
        PlayerTwoHealth.Update();

        OnlineHost? host = _host;
        HostServer? server = host?.Server;
        if (host == null || server == null) return;

        float now = Time.unscaledTime;
        try
        {
            if (server.TakeAutoShootToggles() % 2 == 1) PlayerTwoBridge.ToggleAutoShoot();
            if (server.TakePick(out int offerId, out int pickIndex)) PlayerTwoLoadout.RequestPick(offerId, pickIndex);
            string? loadout = PlayerTwoLoadout.TakeJson();
            if (loadout != null) server.SetLoadout(loadout);
            if (now >= _nextStatus)
            {
                _nextStatus = now + 0.5f;
                HostStatus status = PlayerTwoBridge.ReadStatus();
                if (!PlayerTwoBridge.HooksInstalled) status.Note = "The host's Local Coop DLL is missing the online hooks, so P2 can't be controlled yet.";
                else if (FrameGrabber.Error != null) status.Note = "The host's screen capture has a problem.";
                server.SetHostStatus(status);
                LogNewLinks(host);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedUpdateError)
            {
                _loggedUpdateError = true;
                Plugin.Logger.LogError("Online co-op update step failed: " + ex);
            }
        }
        if (now >= _nextTapCheck)
        {
            _nextTapCheck = now + 1f;
            try
            {
                KeepAudioTapAttached(host);
            }
            catch (Exception ex)
            {
                // Also catches the audio types being missing from this build (thrown when the method is compiled).
                _audioTapBroken = true;
                Plugin.Logger.LogWarning("Game sound can't be streamed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads the panel key through BepInEx's input layer, which uses Unity's old Input class when the
    /// game has it and the Input System package otherwise.
    /// </summary>
    private static bool PanelKeyPressed()
    {
        KeyCode key = OnlineConfig.PanelKey.Value;
        try
        {
            return BepInEx.UnityInput.Current.GetKeyDown(key);
        }
        catch (Exception ex)
        {
            if (!_loggedKeyError)
            {
                _loggedKeyError = true;
                Plugin.Logger.LogWarning($"Can't read the {key} key ({ex.GetType().Name}: {ex.Message}). Use the \"Online Co-op\" button in the top-right corner instead.");
            }
        }
        return false;
    }

    private static void OnEndOfFrame()
    {
        try
        {
            VideoStreamer? video = _host?.Video;
            if (video == null) return;
            double now = HostServer.Now;
            if (video.ShouldCapture(now)) FrameGrabber.Capture(video, now);
        }
        catch (Exception ex)
        {
            if (_loggedCaptureLoopError) return;
            _loggedCaptureLoopError = true;
            Plugin.Logger.LogError("End-of-frame capture step failed: " + ex);
        }
    }

    public static void StartHosting()
    {
        if (IsHosting) return;
        _startError = null;
        try
        {
            var host = new OnlineHost(OnlineConfig.ToOptions(), LoadGuestPage());
            if (!host.Start())
            {
                _startError = host.LastError ?? "Hosting failed; see the BepInEx log.";
                return;
            }
            _host = host;
            PlayerTwoBridge.SetServer(host.Server);
            PlayerTwoLoadout.ResendJson();
            if (OnlineConfig.StreamAudio.Value && FmodTap.TryInstall()) FmodTap.Target = host.Audio;
            else AudioTap.Target = host.Audio;
            _tapCamera = null;
            _nextTapCheck = 0;
            Plugin.Logger.LogInfo($"Hosting online co-op on port {host.Server!.Port}. Join code {host.Server.JoinCode}.");
        }
        catch (Exception ex)
        {
            _startError = ex.Message;
            Plugin.Logger.LogError("Could not start hosting: " + ex);
        }
    }

    public static void StopHosting()
    {
        OnlineHost? host = _host;
        _host = null;
        AudioTap.Target = null;
        FmodTap.Remove();
        PlayerTwoBridge.SetServer(null);
        LoggedLinks.Clear();
        if (host == null) return;
        try
        {
            host.Stop();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Stopping the host: " + ex.Message);
        }
        FrameGrabber.Reset();
        Plugin.Logger.LogInfo("Stopped hosting.");
    }

    /// <summary>Game exit: close the tunnel and the router port.</summary>
    public static void Shutdown()
    {
        try
        {
            AudioTap.Target = null;
            FmodTap.Remove();
            PlayerTwoBridge.SetServer(null);
            OnlineHost? host = _host;
            _host = null;
            host?.Stop();
        }
        catch
        {
        }
    }

    private static byte[] LoadGuestPage()
    {
        using Stream? stream = typeof(OnlineController).Assembly.GetManifestResourceStream("BALLxPITOnlineCoop.client.html");
        if (stream == null) throw new InvalidOperationException("The guest page is missing from the plugin.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void LogNewLinks(OnlineHost host)
    {
        foreach (JoinLink link in host.GetLinks())
        {
            if (link.Label.Contains("starting", StringComparison.OrdinalIgnoreCase)) continue;
            if (LoggedLinks.Add(link.Url)) Plugin.Logger.LogInfo($"{link.Label}: {link.Url}");
        }
    }

    /// <summary>Keeps an <see cref="AudioTap"/> on the object that has the AudioListener (the main camera).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void KeepAudioTapAttached(OnlineHost host)
    {
        if (_audioTapBroken || host.Audio == null || !OnlineConfig.StreamAudio.Value || FmodTap.IsInstalled) return;
        try
        {
            Camera cam = Camera.main;
            if (cam == null || cam == _tapCamera) return;
            GameObject target = cam.gameObject;
            AudioListener listener = target.GetComponent<AudioListener>();
            if (listener == null)
            {
                if (!_loggedNoListener)
                {
                    _loggedNoListener = true;
                    Plugin.Logger.LogWarning("The main camera has no AudioListener, so game sound can't be streamed (the picture still is).");
                }
                return;
            }
            AudioTap.EnsureRegistered();
            if (target.GetComponent<AudioTap>() == null) target.AddComponent<AudioTap>();
            AudioTap.SampleRate = OutputSampleRate();
            _tapCamera = cam;
            Plugin.Logger.LogInfo($"Streaming game sound from '{target.name}' at {AudioTap.SampleRate} Hz.");
        }
        catch (Exception ex)
        {
            _audioTapBroken = true;
            Plugin.Logger.LogWarning("Game sound can't be streamed: " + ex.Message);
        }
    }

    private static int OutputSampleRate()
    {
        try
        {
            return ReadOutputSampleRate();
        }
        catch
        {
            return 48000; // Unity's usual mixer rate
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int ReadOutputSampleRate() => AudioSettings.outputSampleRate;

    // ---- Panel ------------------------------------------------------------------------------
    // Drawn only with GUI.Button, the one IMGUI call Local Coop already proves works in this game.

    public static void OnGUI()
    {
        try
        {
            if (!_loggedFirstGui)
            {
                _loggedFirstGui = true;
                Plugin.Logger.LogInfo("Online co-op on-screen button is being drawn.");
            }
            PlayerTwoHealth.DrawLabel();
            if (_panelVisible) DrawPanel();
            else if (IsHosting || PlayerTwoLoadout.Offer != null || (OnlineConfig.ShowMenuButton.Value && !PlayerTwoBridge.IsPlayerTwoActive())) DrawBadge();
        }
        catch (Exception ex)
        {
            if (_loggedGuiError) return;
            _loggedGuiError = true;
            Plugin.Logger.LogError("Online co-op panel failed to draw: " + ex);
        }
    }

    private static void DrawBadge()
    {
        HostServer? server = _host?.Server;
        int guests = server?.GuestCount ?? 0;
        string text = server == null ? $"Online Co-op ({OnlineConfig.PanelKey.Value})"
            : guests == 0 ? $"Online: waiting ({OnlineConfig.PanelKey.Value})"
            : $"Online: {guests} connected ({OnlineConfig.PanelKey.Value})";
        // Without a guest to pick on their page, the host picks P2's upgrades in the panel.
        if (PlayerTwoLoadout.Offer != null && (server == null || !server.HasPlayer))
            text = $"P2 level-up: pick in the panel ({OnlineConfig.PanelKey.Value})";
        if (GUI.Button(new Rect(Screen.width - 260 - PanelMargin, PanelMargin, 260, RowHeight), text)) _panelVisible = true;
    }

    private static void DrawPanel()
    {
        float width = Math.Min(700f, Screen.width - PanelMargin * 2);
        float x = Screen.width - width - PanelMargin;
        float y = PanelMargin;

        bool Row(string text)
        {
            bool clicked = GUI.Button(new Rect(x, y, width, RowHeight), text);
            y += RowHeight + RowGap;
            return clicked;
        }

        if (Row($"BALL x PIT Online Co-op  ·  {OnlineConfig.PanelKey.Value} hides this")) _panelVisible = false;
        if (!PlayerTwoBridge.HooksInstalled)
            Row("! Your BALLxPITLocalCoop.dll is the original: guests can watch but not play. Use the one from this download.");

        if (PlayerTwoLoadout.Enabled && PlayerTwoBridge.IsPlayerTwoActive() && PlayerTwoLoadout.Balls.Count > 0)
        {
            Row($"P2's balls: {PlayerTwoLoadout.DescribeBalls()}   ·   passives: {PlayerTwoLoadout.DescribePassives()}");
            IReadOnlyList<LoadoutChoice>? offer = PlayerTwoLoadout.Offer;
            if (offer != null)
            {
                int more = PlayerTwoLoadout.PicksWaiting - 1;
                Row(PlayerTwoLoadout.WaitingForLevelUpScreen
                    ? "P2's pick is applied when your level-up screen closes."
                    : $"P2 level-up{(more > 0 ? $" (+{more} more)" : "")}: your friend picks on their page, or click one for P2:");
                float cell = (width - (offer.Count - 1) * RowGap) / offer.Count;
                for (int i = 0; i < offer.Count; i++)
                {
                    if (GUI.Button(new Rect(x + i * (cell + RowGap), y, cell, RowHeight), offer[i].Label))
                        PlayerTwoLoadout.PickFromPanel(i);
                }
                y += RowHeight + RowGap;
            }
        }

        OnlineHost? host = _host;
        HostServer? server = host?.Server;
        if (host == null || server == null)
        {
            if (Row("Host an online game")) StartHosting();
            Row("Friends join in their web browser from the link you'll get here. They don't need the game.");
            if (_startError != null) Row("! " + _startError);
            return;
        }

        List<JoinLink> links = host.GetLinks();
        foreach (JoinLink link in links)
        {
            bool copied = link.Url == _copiedUrl && Time.unscaledTime < _copiedUntil;
            string suffix = copied ? (_copyFailed ? "  (copy failed: it's in the BepInEx log)" : "  (copied!)") : "  (click to copy)";
            if (Row($"{link.Label}: {link.Url}{suffix}"))
            {
                _copyFailed = !Clipboard.TrySetText(link.Url);
                _copiedUrl = link.Url;
                _copiedUntil = Time.unscaledTime + 3f;
                Plugin.Logger.LogInfo($"{link.Label}: {link.Url}");
            }
        }
        if (OnlineConfig.InternetTunnel.Value && !host.TunnelReady) Row("Internet link: " + host.TunnelStatus);
        if (OnlineConfig.Upnp.Value && !host.DirectLinkReady && !host.TunnelReady && host.UpnpStatus.Length > 0) Row("Router: " + host.UpnpStatus);
        Row($"Join code: {server.JoinCode}   ·   send a friend a link above; it already contains the code");

        List<GuestInfo> guests = server.GetGuests();
        if (guests.Count == 0)
        {
            Row("Waiting for someone to open the link...");
        }
        else
        {
            foreach (GuestInfo guest in guests)
                Row($"{(guest.IsPlayer ? "Playing P2" : "Watching")}: {guest.Name}  ·  {guest.PingMs} ms  ·  {guest.KbitPerSecond / 1000:0.0} Mbit/s"
                    + (guest.Congested ? "  ·  connection full" : ""));
        }
        if (!PlayerTwoBridge.IsPlayerTwoActive()) Row("P2 appears when a run starts.");

        if (OnlineConfig.StreamAudio.Value && guests.Count > 0)
            Row("Sound: " + (FmodTap.IsInstalled ? FmodTap.Status : _tapCamera != null ? "Unity audio listener" : FmodTap.Status.Length > 0 ? FmodTap.Status : "not found yet"));

        VideoStreamer? video = host.Video;
        if (FrameGrabber.Error != null) Row("! Screen capture: " + FrameGrabber.Error);
        else if (video != null && guests.Count > 0)
            Row($"Picture {video.OutputWidth}x{video.OutputHeight}  ·  {video.FramesPerSecond} fps  ·  quality {video.CurrentQuality}  ·  {video.LastFrameBytes / 1024} KB/frame  ·  encode {video.LastEncodeMs:0} ms");

        float third = (width - 2 * RowGap) / 3f;
        if (GUI.Button(new Rect(x, y, third, RowHeight), "Kick P2")) server.KickPlayer();
        if (GUI.Button(new Rect(x + third + RowGap, y, third, RowHeight), "New code (kicks nobody)")) { server.RegenerateCode(); LoggedLinks.Clear(); }
        if (GUI.Button(new Rect(x + 2 * (third + RowGap), y, third, RowHeight), "Stop hosting")) StopHosting();
    }
}
