using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using BALLxPITOnlineCoop.Core;

namespace BALLxPITOnlineCoop.Game;

/// <summary>
/// BALL x PIT plays its sound through FMOD, which Unity's AudioListener never hears. This adds a
/// pass-through effect (a custom DSP) at the front of FMOD's master bus, the last stop before the
/// speakers: FMOD hands it the finished mix, it copies the samples to the stream and passes them on
/// unchanged. Everything goes straight to fmod.dll's exported C functions; the only thing taken from
/// the game is the handle of its FMOD system (FMODUnity.RuntimeManager.CoreSystem).
/// </summary>
internal static unsafe class FmodTap
{
    private const uint PluginSdkVersion = 110; // FMOD_PLUGIN_SDK_VERSION for FMOD 2.x
    private const int ResultOk = 0;
    private const int DspIndexHead = 0; // the DSP that feeds the output, i.e. after everything else

    public static volatile AudioStreamer? Target;
    private static volatile int _sampleRate = 48000;

    private static IntPtr _system, _masterGroup, _dsp;
    private static DspDescription* _description;
    private static delegate* unmanaged<IntPtr, IntPtr, int> _removeDsp;
    private static delegate* unmanaged<IntPtr, int> _releaseDsp;

    public static bool IsInstalled => _dsp != IntPtr.Zero;
    public static string Status { get; private set; } = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct DspDescription
    {
        public uint PluginSdkVersion;
        public fixed byte Name[32];
        public uint Version;
        public int NumInputBuffers;
        public int NumOutputBuffers;
        public IntPtr Create, Release, Reset, Read, Process, SetPosition;
        public int NumParameters;
        public IntPtr ParamDesc;
        public IntPtr SetParameterFloat, SetParameterInt, SetParameterBool, SetParameterData;
        public IntPtr GetParameterFloat, GetParameterInt, GetParameterBool, GetParameterData;
        public IntPtr ShouldIProcess;
        public IntPtr UserData;
        public IntPtr SysRegister, SysDeregister, SysMix;
    }

    /// <summary>Game thread. Returns false (with <see cref="Status"/> saying why) if FMOD isn't reachable.</summary>
    public static bool TryInstall()
    {
        if (IsInstalled) return true;
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Fail("not Windows");
            IntPtr module = GetModuleHandleW("fmod.dll");
            if (module == IntPtr.Zero) module = GetModuleHandleW("fmodL.dll");
            if (module == IntPtr.Zero) return Fail("fmod.dll isn't loaded");

            var getMaster = (delegate* unmanaged<IntPtr, IntPtr*, int>)Export(module, "FMOD5_System_GetMasterChannelGroup");
            var createDsp = (delegate* unmanaged<IntPtr, DspDescription*, IntPtr*, int>)Export(module, "FMOD5_System_CreateDSP");
            var addDsp = (delegate* unmanaged<IntPtr, int, IntPtr, int>)Export(module, "FMOD5_ChannelGroup_AddDSP");
            var getFormat = (delegate* unmanaged<IntPtr, int*, int*, int*, int>)Export(module, "FMOD5_System_GetSoftwareFormat");
            _removeDsp = (delegate* unmanaged<IntPtr, IntPtr, int>)Export(module, "FMOD5_ChannelGroup_RemoveDSP");
            _releaseDsp = (delegate* unmanaged<IntPtr, int>)Export(module, "FMOD5_DSP_Release");

            IntPtr system = CoreSystemHandle();
            if (system == IntPtr.Zero) return Fail("couldn't get the game's FMOD system");

            int rate = 0, speakerMode = 0, rawSpeakers = 0;
            if (getFormat(system, &rate, &speakerMode, &rawSpeakers) == ResultOk && rate > 0) _sampleRate = rate;

            IntPtr master;
            int result = getMaster(system, &master);
            if (result != ResultOk || master == IntPtr.Zero) return Fail($"GetMasterChannelGroup returned {result}");

            // Kept for the life of the process in case FMOD holds on to the description.
            if (_description == null)
            {
                _description = (DspDescription*)Marshal.AllocHGlobal(sizeof(DspDescription));
                *_description = new DspDescription
                {
                    PluginSdkVersion = PluginSdkVersion,
                    Version = 0x00010000,
                    NumInputBuffers = 1,
                    NumOutputBuffers = 1,
                    Read = (IntPtr)(delegate* unmanaged<IntPtr, float*, float*, uint, int, int*, int>)&Read,
                };
                byte[] name = Encoding.ASCII.GetBytes("BALLxPIT Online Coop");
                for (int i = 0; i < name.Length && i < 31; i++) _description->Name[i] = name[i];
            }

            IntPtr dsp;
            result = createDsp(system, _description, &dsp);
            if (result != ResultOk || dsp == IntPtr.Zero) return Fail($"CreateDSP returned {result}");
            result = addDsp(master, DspIndexHead, dsp);
            if (result != ResultOk)
            {
                _releaseDsp(dsp);
                return Fail($"AddDSP returned {result}");
            }

            _system = system;
            _masterGroup = master;
            _dsp = dsp;
            Status = $"FMOD mix at {_sampleRate} Hz";
            Plugin.Logger.LogInfo($"Streaming game sound from FMOD's master bus ({_sampleRate} Hz).");
            return true;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static void Remove()
    {
        Target = null;
        IntPtr dsp = _dsp;
        if (dsp == IntPtr.Zero) return;
        _dsp = IntPtr.Zero;
        try
        {
            _removeDsp(_masterGroup, dsp);
            _releaseDsp(dsp);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Removing the FMOD sound tap: " + ex.Message);
        }
    }

    private static bool Fail(string why)
    {
        Status = "FMOD capture unavailable: " + why;
        Plugin.Logger.LogInfo(Status);
        return false;
    }

    /// <summary>FMOD's mixer thread: copy the mix through unchanged and hand it to the stream.</summary>
    [UnmanagedCallersOnly]
    private static int Read(IntPtr state, float* input, float* output, uint length, int inChannels, int* outChannels)
    {
        try
        {
            int outCh = outChannels != null ? *outChannels : inChannels;
            if (outCh == inChannels)
            {
                long bytes = (long)length * inChannels * sizeof(float);
                Buffer.MemoryCopy(input, output, bytes, bytes);
            }
            else
            {
                int shared = Math.Min(outCh, inChannels);
                for (uint i = 0; i < length; i++)
                {
                    for (int c = 0; c < outCh; c++)
                        output[i * outCh + c] = c < shared ? input[i * inChannels + c] : 0f;
                }
            }
            AudioStreamer? target = Target;
            if (target != null && inChannels > 0)
                target.Push(new ReadOnlySpan<float>(input, checked((int)(length * inChannels))), inChannels, _sampleRate);
        }
        catch
        {
            // Never break the host's sound.
        }
        return ResultOk;
    }

    /// <summary>FMODUnity.RuntimeManager.CoreSystem.handle, read by reflection (no compile-time FMOD dependency).</summary>
    private static IntPtr CoreSystemHandle()
    {
        Type? runtimeManager = Type.GetType("FMODUnity.RuntimeManager, FMODUnity");
        PropertyInfo? coreSystem = runtimeManager?.GetProperty("CoreSystem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? system = coreSystem?.GetValue(null);
        if (system == null) return IntPtr.Zero;
        Type type = system.GetType();
        object? handle = type.GetField("handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(system)
            ?? type.GetProperty("handle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(system);
        return handle is IntPtr pointer ? pointer : IntPtr.Zero;
    }

    private static IntPtr Export(IntPtr module, string name)
    {
        IntPtr address = GetProcAddress(module, name);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name + " isn't exported by fmod.dll");
        return address;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}
