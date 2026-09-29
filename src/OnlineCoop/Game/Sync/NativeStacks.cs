using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace BALLxPITOnlineCoop.Game.Sync;

/// <summary>
/// Names the game code on the current thread's stack. The game's build has no managed stack traces,
/// so this walks the native stack and matches each return address inside GameAssembly.dll to the
/// method whose compiled code starts closest below it, from a table of every method's code address
/// made once up front. Code the table doesn't list (generic instances, runtime helpers) shows up as
/// the listed method before it, with a large offset.
/// </summary>
internal static unsafe class NativeStacks
{
    [DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint framesToSkip, uint framesToCapture, IntPtr* backTrace, IntPtr backTraceHash);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    private static ulong _moduleStart, _moduleEnd;
    private static ulong[] _starts = Array.Empty<ulong>();
    private static string[] _names = Array.Empty<string>();
    private static volatile bool _ready;

    public static bool Ready => _ready;

    /// <summary>Game thread, once: the code-address table. Returns what it found, for the log.</summary>
    public static string Prepare()
    {
        if (_ready) return $"stack table already made ({_starts.Length} methods)";
        var watch = Stopwatch.StartNew();
        IntPtr module = GetModuleHandleW("GameAssembly.dll");
        if (module == IntPtr.Zero) return "stack table: GameAssembly.dll not found";
        int peHeader = *(int*)((byte*)module + 0x3c);
        uint imageSize = *(uint*)((byte*)module + peHeader + 24 + 56);
        _moduleStart = (ulong)module;
        _moduleEnd = _moduleStart + imageSize;

        var entries = new List<(ulong Start, string Name)>(200_000);
        uint assemblyCount = 0;
        IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref assemblyCount);
        for (uint a = 0; a < assemblyCount; a++)
        {
            IntPtr image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
            if (image == IntPtr.Zero) continue;
            uint classCount = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < classCount; c++)
            {
                IntPtr klass = IL2CPP.il2cpp_image_get_class(image, c);
                if (klass == IntPtr.Zero) continue;
                string? className = null;
                IntPtr iter = IntPtr.Zero;
                IntPtr method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    // MethodInfo starts with the pointer to the method's compiled code.
                    ulong code = (ulong)*(IntPtr*)method;
                    if (code < _moduleStart || code >= _moduleEnd) continue;
                    className ??= ClassName(klass);
                    entries.Add((code, className + "." + (Marshal.PtrToStringUTF8(IL2CPP.il2cpp_method_get_name(method)) ?? "?")));
                }
            }
        }
        entries.Sort((x, y) => x.Start.CompareTo(y.Start));
        _starts = new ulong[entries.Count];
        _names = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            _starts[i] = entries[i].Start;
            _names[i] = entries[i].Name;
        }
        _ready = true;
        return $"stack table: {entries.Count} methods from {assemblyCount} assemblies in {watch.ElapsedMilliseconds} ms";
    }

    private static string ClassName(IntPtr klass)
    {
        string name = IL2CPP.il2cpp_class_get_name_(klass) ?? "?";
        IntPtr outer = IL2CPP.il2cpp_class_get_declaring_type(klass);
        if (outer != IntPtr.Zero) return ClassName(outer) + "/" + name;
        string? space = IL2CPP.il2cpp_class_get_namespace_(klass);
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    /// <summary>Any thread: "ThreadSafeRandom.RandomRange+0x1c &lt;- HeroInst.PickDamage+0x40 &lt;- ...".</summary>
    public static string Describe(int maxFrames = 14)
    {
        if (!_ready) return "(no stack table)";
        IntPtr* frames = stackalloc IntPtr[64];
        int count = RtlCaptureStackBackTrace(0, 64, frames, IntPtr.Zero);
        var parts = new List<string>();
        for (int i = 0; i < count && parts.Count < maxFrames; i++)
        {
            ulong address = (ulong)frames[i];
            if (address < _moduleStart || address >= _moduleEnd) continue;
            int at = Array.BinarySearch(_starts, address);
            if (at < 0) at = ~at - 1;
            parts.Add(at < 0 ? $"GameAssembly+0x{address - _moduleStart:x}" : $"{_names[at]}+0x{address - _starts[at]:x}");
        }
        return parts.Count == 0 ? "(no game code on the stack)" : string.Join(" <- ", parts);
    }
}
