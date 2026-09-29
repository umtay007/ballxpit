using BALLxPITOnlineCoop.Core;
using BepInEx.Configuration;
using UnityEngine;

namespace BALLxPITOnlineCoop.Game;

internal static class OnlineConfig
{
    public static ConfigEntry<KeyCode> PanelKey = null!;
    public static ConfigEntry<bool> ShowMenuButton = null!;
    public static ConfigEntry<bool> HostOnStartup = null!;
    public static ConfigEntry<string> HostName = null!;
    public static ConfigEntry<int> Port = null!;
    public static ConfigEntry<bool> InternetTunnel = null!;
    public static ConfigEntry<bool> DownloadCloudflared = null!;
    public static ConfigEntry<bool> Upnp = null!;
    public static ConfigEntry<int> MaxGuests = null!;
    public static ConfigEntry<int> MaxFps = null!;
    public static ConfigEntry<int> MaxHeight = null!;
    public static ConfigEntry<int> Quality = null!;
    public static ConfigEntry<bool> AdaptiveQuality = null!;
    public static ConfigEntry<bool> StreamAudio = null!;
    public static ConfigEntry<int> EncoderThreads = null!;
    public static ConfigEntry<bool> SeparateHealth = null!;
    public static ConfigEntry<int> DownedSeconds = null!;
    public static ConfigEntry<int> ReviveHealthPercent = null!;
    public static ConfigEntry<bool> SharedHealing = null!;
    public static ConfigEntry<bool> SeparateLoadout = null!;

    public static void Bind(ConfigFile config)
    {
        PanelKey = config.Bind("Hosting", "PanelKey", KeyCode.F8, "Shows or hides the online co-op panel.");
        ShowMenuButton = config.Bind("Hosting", "ShowMenuButton", true,
            "Show a small \"Online Co-op\" button in the top-right corner outside of runs. Clicking it opens the panel, same as the PanelKey.");
        HostOnStartup = config.Bind("Hosting", "HostOnStartup", false, "Start hosting as soon as the game starts, without opening the panel.");
        HostName = config.Bind("Hosting", "HostName", "Host", "The name guests see when they connect.");
        Port = config.Bind("Hosting", "Port", 47777, "TCP port the game listens on. If it's taken the next free one (up to +9) is used.");
        MaxGuests = config.Bind("Hosting", "MaxGuests", 4, "How many people may be connected at once: the one playing P2 plus spectators.");

        InternetTunnel = config.Bind("Internet", "CloudflareTunnel", true,
            "Make an https://....trycloudflare.com link through Cloudflare's free quick tunnels. Works from anywhere, "
            + "with no router setup or port forwarding.");
        DownloadCloudflared = config.Bind("Internet", "DownloadCloudflared", true,
            "Download cloudflared.exe (Cloudflare's official tunnel program, from github.com/cloudflare/cloudflared) into the "
            + "plugin folder the first time it's needed. Turn off to place it there yourself.");
        Upnp = config.Bind("Internet", "UPnP", true,
            "Ask your router (UPnP) to forward the port so a direct link, with a little less lag, is also offered. "
            + "The port is closed again when you stop hosting.");

        MaxFps = config.Bind("Stream", "MaxFps", 30, new ConfigDescription("Most frames per second sent to guests.", new AcceptableValueRange<int>(5, 60)));
        MaxHeight = config.Bind("Stream", "MaxHeight", 540,
            new ConfigDescription("Pictures taller than this are shrunk by a whole-number factor (1080 lines become 540). "
                + "It shrinks further by itself while a guest's connection can't keep up.", new AcceptableValueRange<int>(180, 2160)));
        Quality = config.Bind("Stream", "Quality", 60, new ConfigDescription("JPEG quality of the picture.", new AcceptableValueRange<int>(10, 95)));
        AdaptiveQuality = config.Bind("Stream", "AdaptiveQuality", true, "Lower the quality (and then the size) automatically while a guest's connection can't keep up.");
        StreamAudio = config.Bind("Stream", "StreamAudio", true, "Send the game's sound to guests.");
        EncoderThreads = config.Bind("Stream", "EncoderThreads", 0, "CPU threads for picture encoding. 0 = half your cores, up to 6.");

        SeparateHealth = config.Bind("Co-op", "SeparateHealth", true,
            "P2 has its own health instead of sharing yours. At zero P2 is knocked out for a while instead of ending the run. "
            + "Works for online and local co-op.");
        DownedSeconds = config.Bind("Co-op", "DownedSeconds", 20,
            new ConfigDescription("How long a knocked-out P2 stays down.", new AcceptableValueRange<int>(1, 600)));
        ReviveHealthPercent = config.Bind("Co-op", "ReviveHealthPercent", 50,
            new ConfigDescription("Health P2 gets back up with, in percent of max health.", new AcceptableValueRange<int>(1, 100)));
        SharedHealing = config.Bind("Co-op", "SharedHealing", true,
            "When you heal, P2 heals by the same amount (hearts may only be collectable by you).");
        SeparateLoadout = config.Bind("Co-op", "SeparateLoadout", true,
            "P2 has its own balls, passives and stats, and picks its own upgrade every time you level up (on the guest's "
            + "page, or in this panel). Off: P2 uses copies of your balls, as in Local Coop. Works for online and local co-op.");
    }

    public static OnlineHostOptions ToOptions() => new()
    {
        Port = Port.Value,
        UseCloudflareTunnel = InternetTunnel.Value,
        AllowCloudflaredDownload = DownloadCloudflared.Value,
        UseUpnp = Upnp.Value,
        MaxGuests = MaxGuests.Value,
        MaxFps = MaxFps.Value,
        MaxHeight = MaxHeight.Value,
        Quality = Quality.Value,
        AdaptiveQuality = AdaptiveQuality.Value,
        StreamAudio = StreamAudio.Value,
        EncoderThreads = EncoderThreads.Value,
        ToolDirectory = Plugin.PluginDirectory,
        HostName = string.IsNullOrWhiteSpace(HostName.Value) ? "Host" : HostName.Value.Trim(),
    };
}
