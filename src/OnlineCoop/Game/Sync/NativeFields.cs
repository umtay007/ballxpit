using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;

namespace BALLxPITOnlineCoop.Game.Sync;

/// <summary>Reads IL2CPP objects' fields straight from memory, for fingerprints and the setup list.</summary>
internal static unsafe class NativeFields
{
    /// <summary>
    /// A fingerprint of a random number generator's whole state, whatever the runtime calls its fields:
    /// every number field, the contents of number arrays (the seed table), and the same for objects it
    /// refers to (newer runtimes keep the state in a helper object). Addresses are never mixed in, as
    /// they differ from run to run. Two generators with the same fingerprint hand out the same numbers.
    /// </summary>
    public static uint RandomState(IntPtr random) => random == IntPtr.Zero ? 0 : HashObject(random, 2);

    private sealed record FieldLayout(int Offset, int Kind, int Size);

    private static readonly Dictionary<IntPtr, FieldLayout[]> Layouts = new();

    public static uint HashObject(IntPtr obj, int depth)
    {
        uint hash = 2166136261;
        HashInto(obj, depth, ref hash);
        return hash;
    }

    private static void Mix(ref uint hash, byte* data, int length)
    {
        unchecked
        {
            for (int i = 0; i < length; i++) hash = (hash ^ data[i]) * 16777619;
        }
    }

    private static void HashInto(IntPtr obj, int depth, ref uint hash)
    {
        if (obj == IntPtr.Zero) return;
        foreach (FieldLayout field in LayoutOf(IL2CPP.il2cpp_object_get_class(obj)))
        {
            byte* at = (byte*)obj + field.Offset;
            if (field.Size > 0)
            {
                Mix(ref hash, at, field.Size);
            }
            else if (field.Kind == 0x1d)
            {
                IntPtr array = *(IntPtr*)at;
                if (array == IntPtr.Zero) continue;
                IntPtr arrayClass = IL2CPP.il2cpp_object_get_class(array);
                IntPtr elementClass = IL2CPP.il2cpp_class_get_element_class(arrayClass);
                if (elementClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(elementClass)) continue;
                int elementSize = IL2CPP.il2cpp_class_array_element_size(arrayClass);
                long bytes = (long)IL2CPP.il2cpp_array_length(array) * elementSize;
                Mix(ref hash, (byte*)array + 4 * IntPtr.Size, (int)Math.Min(bytes, 4096));
            }
            else if (depth > 0)
            {
                HashInto(*(IntPtr*)at, depth - 1, ref hash);
            }
        }
    }

    /// <summary>Instance fields of a class and its bases: numbers with their size, arrays, and references to follow.</summary>
    private static FieldLayout[] LayoutOf(IntPtr klass)
    {
        if (Layouts.TryGetValue(klass, out FieldLayout[]? cached)) return cached;
        var fields = new List<FieldLayout>();
        for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            IntPtr iter = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
            {
                int flags = IL2CPP.il2cpp_field_get_flags(field);
                if ((flags & 0x10) != 0 || (flags & 0x40) != 0) continue;
                int offset = (int)IL2CPP.il2cpp_field_get_offset(field);
                if (offset <= 0) continue;
                int kind = IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_field_get_type(field));
                int size = kind switch
                {
                    0x02 or 0x04 or 0x05 => 1,
                    0x03 or 0x06 or 0x07 => 2,
                    0x08 or 0x09 or 0x0c => 4,
                    0x0a or 0x0b or 0x0d => 8,
                    _ => 0,
                };
                // Numbers, number arrays, and references to plain objects; strings and the rest are skipped.
                if (size > 0 || kind == 0x1d || kind == 0x12 || kind == 0x1c) fields.Add(new FieldLayout(offset, kind, size));
            }
        }
        FieldLayout[] result = fields.ToArray();
        Layouts[klass] = result;
        return result;
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
