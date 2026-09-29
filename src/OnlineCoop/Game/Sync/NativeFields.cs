using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;

namespace BALLxPITOnlineCoop.Game.Sync;

/// <summary>Reads IL2CPP object fields by name, for fields the interop assemblies don't expose.</summary>
internal static unsafe class NativeFields
{
    private static readonly Dictionary<(IntPtr Class, string Name), int> Offsets = new();

    /// <summary>Byte offset of an instance field, searching base classes too; -1 if there is none.</summary>
    public static int Offset(IntPtr obj, string name)
    {
        if (obj == IntPtr.Zero) return -1;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
        if (Offsets.TryGetValue((klass, name), out int cached)) return cached;
        int offset = -1;
        for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(k, name);
            if (field == IntPtr.Zero) continue;
            offset = (int)IL2CPP.il2cpp_field_get_offset(field);
            break;
        }
        Offsets[(klass, name)] = offset;
        return offset;
    }

    public static int ReadInt(IntPtr obj, string name, int fallback = 0)
    {
        int offset = Offset(obj, name);
        return offset <= 0 ? fallback : *(int*)((byte*)obj + offset);
    }

    public static IntPtr ReadPointer(IntPtr obj, string name)
    {
        int offset = Offset(obj, name);
        return offset <= 0 ? IntPtr.Zero : *(IntPtr*)((byte*)obj + offset);
    }

    /// <summary>
    /// A fingerprint of a System.Random: its whole seed array plus its two positions. Two generators
    /// with the same fingerprint hand out the same numbers from here on.
    /// </summary>
    public static uint RandomState(IntPtr random)
    {
        if (random == IntPtr.Zero) return 0;
        uint hash = 2166136261;
        void Mix(int v)
        {
            unchecked
            {
                hash = (hash ^ (uint)v) * 16777619;
            }
        }
        Mix(ReadInt(random, "inext", -1));
        Mix(ReadInt(random, "inextp", -1));
        IntPtr array = ReadPointer(random, "SeedArray");
        if (array != IntPtr.Zero)
        {
            int length = (int)IL2CPP.il2cpp_array_length(array);
            int* data = (int*)((byte*)array + 4 * IntPtr.Size);
            for (int i = 0; i < length && i < 64; i++) Mix(data[i]);
        }
        return hash;
    }

    /// <summary>Every number field declared on the object's class called <paramref name="className"/>, as text.</summary>
    public static List<(string Name, string Value)> DescribeNumbers(IntPtr obj, string className)
    {
        var result = new List<(string, string)>();
        if (obj == IntPtr.Zero) return result;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
        while (klass != IntPtr.Zero && IL2CPP.il2cpp_class_get_name_(klass) != className)
            klass = IL2CPP.il2cpp_class_get_parent(klass);
        if (klass == IntPtr.Zero) return result;
        IntPtr iter = IntPtr.Zero;
        IntPtr field;
        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
        {
            int flags = IL2CPP.il2cpp_field_get_flags(field);
            if ((flags & 0x10) != 0 || (flags & 0x40) != 0) continue;
            int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
            if (offset <= 0) continue;
            byte* at = (byte*)obj + offset;
            string? value = IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_field_get_type(field)) switch
            {
                0x02 => (*at != 0) ? "true" : "false",
                0x04 => (*(sbyte*)at).ToString(CultureInfo.InvariantCulture),
                0x05 => (*at).ToString(CultureInfo.InvariantCulture),
                0x06 => (*(short*)at).ToString(CultureInfo.InvariantCulture),
                0x07 => (*(ushort*)at).ToString(CultureInfo.InvariantCulture),
                0x08 => (*(int*)at).ToString(CultureInfo.InvariantCulture),
                0x09 => (*(uint*)at).ToString(CultureInfo.InvariantCulture),
                0x0a => (*(long*)at).ToString(CultureInfo.InvariantCulture),
                0x0b => (*(ulong*)at).ToString(CultureInfo.InvariantCulture),
                0x0c => (*(float*)at).ToString("R", CultureInfo.InvariantCulture),
                0x0d => (*(double*)at).ToString("R", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (value != null) result.Add((IL2CPP.il2cpp_field_get_name_(field) ?? "?", value));
        }
        return result;
    }
}
