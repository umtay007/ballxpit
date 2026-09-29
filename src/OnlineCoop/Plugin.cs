using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using BALLxPITOnlineCoop.Core;
using BALLxPITOnlineCoop.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

// The add-on drives fields and methods that are private in "BALLxPIT: Local Coop". It compiles
// against a publicized copy of that assembly; this attribute lets the .NET runtime allow the same
// access against the real one.
[assembly: IgnoresAccessChecksTo("BALLxPITLocalCoop")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}

namespace BALLxPITOnlineCoop
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(LocalCoopGuid)]
    public sealed class Plugin : BasePlugin
    {
        public const string PluginGuid = "ballxpit.onlinecoop";
        public const string PluginName = "BALLxPIT: Online Coop";
        public const string PluginVersion = "0.1.8";
        public const string LocalCoopGuid = "sparrow.ballxpit.localcoop";

        private static readonly ConcurrentQueue<(Core.LogLevel Level, string Message)> PendingLogs = new();

        internal static ManualLogSource Logger { get; private set; } = null!;
        internal static string PluginDirectory { get; private set; } = "";

        public override void Load()
        {
            Logger = Log;
            PluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? Paths.PluginPath;
            // Networking threads log through a queue that the game thread empties.
            Core.Log.Sink = (level, message) => PendingLogs.Enqueue((level, message));

            var config = new ConfigFile(Path.Combine(Paths.ConfigPath, "BALLxPITOnlineCoop.cfg"), true);
            OnlineConfig.Bind(config);

            PlayerTwoBridge.Install();
            var harmony = new HarmonyLib.Harmony(PluginGuid);
            PlayerTwoHealth.Install(harmony);
            PlayerTwoLoadout.Install(harmony);
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<OnlineCoopBehaviour>();
                var host = new GameObject("BALLxPITOnlineCoop");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<OnlineCoopBehaviour>();
            }
            catch (Exception ex)
            {
                Log.LogError("Could not create the online co-op component, so the panel won't appear: " + ex);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => OnlineController.Shutdown();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. In the game press {OnlineConfig.PanelKey.Value}, "
                + "or click the \"Online Co-op\" button in the top-right corner of the menus.");
        }

        /// <summary>Writes queued background-thread messages to the BepInEx log. Game thread only.</summary>
        internal static void FlushLogs()
        {
            while (PendingLogs.TryDequeue(out var entry))
            {
                switch (entry.Level)
                {
                    case Core.LogLevel.Debug: Logger.LogDebug(entry.Message); break;
                    case Core.LogLevel.Info: Logger.LogInfo(entry.Message); break;
                    case Core.LogLevel.Warning: Logger.LogWarning(entry.Message); break;
                    default: Logger.LogError(entry.Message); break;
                }
            }
        }
    }
}
