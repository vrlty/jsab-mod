using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Il2CppInterop.Runtime;

namespace JsabPractice;

// Development aid for reading the game's code: the IL2CPP runtime's own view of the game's
// classes, for typing the decompiled GameAssembly.dll (tools/ghidra/ApplyLayout.java).
// GET /il2cpp/layout writes <levels folder>/_il2cpp-layout.json:
//   { base, classes: [ { name, parent, declaring, valueType, enum, size,
//       fields: [ { name, offset, type, kind, static } ],
//       methods: [ { name, rva, static, ret, params: [ { name, type, kind } ] } ] } ] }
// name is "Namespace.Outer/Inner"; offsets are the runtime's (for value types they include
// the 0x10 object header; static offsets are into the class's static fields); rva is the
// method's code relative to GameAssembly.dll (0 when it has none); kind is the IL2CPP type
// code (Il2CppTypeEnum: 2 bool, 8 int, 0xc float, 0x11 value type, 0x12 class, ...).
//
// Only the native API: no interop wrappers, so no static constructors run (MyMath's throws).
// Open generic definitions are skipped (the runtime can't lay them out).
internal static class LayoutDump
{
    private static readonly string[] Images = { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" };
    private const int FieldStatic = 0x10, FieldLiteral = 0x40, MethodStatic = 0x10;

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    private static string Str(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);

    internal static string FullName(IntPtr klass)
    {
        string name = Str(IL2CPP.il2cpp_class_get_name(klass)), ns = Str(IL2CPP.il2cpp_class_get_namespace(klass));
        IntPtr outer = IL2CPP.il2cpp_class_get_declaring_type(klass);
        if (outer != IntPtr.Zero) return FullName(outer) + "/" + name;
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static object TypeInfo(IntPtr type, string paramName = null)
    {
        int kind = IL2CPP.il2cpp_type_get_type(type);
        return new { name = paramName, type = Str(IL2CPP.il2cpp_type_get_name(type)), kind };
    }

    internal static unsafe string Dump()
    {
        long gaBase = (long)GetModuleHandle("GameAssembly.dll");
        var classes = new List<object>();
        int skipped = 0;
        uint count = 0;
        IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (uint a = 0; a < count; a++)
        {
            IntPtr image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
            if (Array.IndexOf(Images, Str(IL2CPP.il2cpp_image_get_name(image))) < 0)
                continue;
            uint n = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint i = 0; i < n; i++)
            {
                IntPtr klass = IL2CPP.il2cpp_image_get_class(image, i);
                string name = FullName(klass);
                if (name.Contains('`') || name.Contains('<')) { skipped++; continue; }
                try { classes.Add(DumpClass(klass, name, gaBase)); }
                catch (Exception e) { skipped++; Plugin.Logger.LogWarning($"[layout] {name}: {e.Message}"); }
            }
        }

        string path = Path.Combine(LevelDump.RootDir, "_il2cpp-layout.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { @base = gaBase, classes }));
        Plugin.Logger.LogInfo($"[layout] {classes.Count} classes ({skipped} skipped) -> {path}");
        return JsonSerializer.Serialize(new { ok = true, path, classes = classes.Count, skipped });
    }

    // GET /il2cpp/usages: resolves the metadata-usage slots listed in
    // <levels folder>/_il2cpp-usage-slots.json (tools/ghidra/ListUsageSlots.java) to what they
    // hold at runtime, into _il2cpp-usages.json: [{ rva, kind, name }] with kind class / type /
    // method / field / string. An unresolved slot (encoded: usage type << 29 | index << 1 | 1)
    // is resolved the way the game does on a method's first run, by calling
    // il2cpp_codegen_initialize_runtime_metadata (RVA 0x219300) on it: that looks the item up,
    // it runs no static constructors.
    private const long InitRuntimeMetadataRva = 0x219300;

    internal static unsafe string Usages()
    {
        long gaBase = (long)GetModuleHandle("GameAssembly.dll");
        string inPath = Path.Combine(LevelDump.RootDir, "_il2cpp-usage-slots.json");
        if (!File.Exists(inPath))
            return JsonSerializer.Serialize(new { ok = false, error = $"no {inPath} (tools/decomp.py --usages writes it)" });
        var init = (delegate* unmanaged<IntPtr, void>)(gaBase + InitRuntimeMetadataRva);
        var result = new List<object>();
        int failed = 0;
        using (var doc = JsonDocument.Parse(File.ReadAllText(inPath)))
        {
            foreach (var slot in doc.RootElement.EnumerateArray())
            {
                long rva = slot.GetProperty("rva").GetInt64(), encoded = slot.GetProperty("encoded").GetInt64();
                IntPtr addr = (IntPtr)(gaBase + rva);
                int usage = (int)((encoded >> 29) & 7);
                try
                {
                    if ((Marshal.ReadInt64(addr) & 1) == 1) init(addr);
                    IntPtr item = Marshal.ReadIntPtr(addr);
                    if (item == IntPtr.Zero || ((long)item & 1) == 1) { failed++; continue; }
                    (string kind, string name) = usage switch
                    {
                        1 => ("class", FullName(item)),
                        2 => ("type", Str(IL2CPP.il2cpp_type_get_name(item))),
                        3 or 6 => ("method", FullName(IL2CPP.il2cpp_method_get_class(item)) + "::" + Str(IL2CPP.il2cpp_method_get_name(item))),
                        4 => ("field", FullName(IL2CPP.il2cpp_field_get_parent(item)) + "::" + Str(IL2CPP.il2cpp_field_get_name(item))),
                        5 => ("string", new string(IL2CPP.il2cpp_string_chars(item), 0, IL2CPP.il2cpp_string_length(item))),
                        _ => (null, null),
                    };
                    if (kind == null) { failed++; continue; }
                    result.Add(new { rva, kind, name });
                }
                catch (Exception e) { failed++; Plugin.Logger.LogWarning($"[layout] usage slot 0x{rva:x}: {e.Message}"); }
            }
        }
        string path = Path.Combine(LevelDump.RootDir, "_il2cpp-usages.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result));
        Plugin.Logger.LogInfo($"[layout] {result.Count} usage slots resolved ({failed} not) -> {path}");
        return JsonSerializer.Serialize(new { ok = true, path, resolved = result.Count, failed });
    }

    private static object DumpClass(IntPtr klass, string name, long gaBase)
    {
        bool valueType = IL2CPP.il2cpp_class_is_valuetype(klass);
        uint align = 0;
        int size = valueType ? IL2CPP.il2cpp_class_value_size(klass, ref align) : IL2CPP.il2cpp_class_instance_size(klass);
        IntPtr parent = IL2CPP.il2cpp_class_get_parent(klass);

        var fields = new List<object>();
        IntPtr iter = IntPtr.Zero, f;
        while ((f = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
        {
            int flags = IL2CPP.il2cpp_field_get_flags(f);
            if ((flags & FieldLiteral) != 0) continue;   // const: no storage
            IntPtr type = IL2CPP.il2cpp_field_get_type(f);
            fields.Add(new
            {
                name = Str(IL2CPP.il2cpp_field_get_name(f)),
                offset = IL2CPP.il2cpp_field_get_offset(f),
                type = Str(IL2CPP.il2cpp_type_get_name(type)),
                kind = IL2CPP.il2cpp_type_get_type(type),
                @static = (flags & FieldStatic) != 0,
            });
        }

        var methods = new List<object>();
        iter = IntPtr.Zero;
        IntPtr m;
        while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            uint iflags = 0;
            uint flags = IL2CPP.il2cpp_method_get_flags(m, ref iflags);
            long code = (long)Marshal.ReadIntPtr(m);   // MethodInfo.methodPointer
            uint pc = IL2CPP.il2cpp_method_get_param_count(m);
            var ps = new List<object>();
            for (uint p = 0; p < pc; p++)
                ps.Add(TypeInfo(IL2CPP.il2cpp_method_get_param(m, p), Str(IL2CPP.il2cpp_method_get_param_name(m, p))));
            methods.Add(new
            {
                name = Str(IL2CPP.il2cpp_method_get_name(m)),
                rva = code == 0 ? 0 : code - gaBase,
                @static = (flags & MethodStatic) != 0,
                ret = TypeInfo(IL2CPP.il2cpp_method_get_return_type(m)),
                @params = ps,
            });
        }

        return new
        {
            name,
            parent = parent == IntPtr.Zero ? null : FullName(parent),
            valueType,
            @enum = IL2CPP.il2cpp_class_is_enum(klass),
            size,
            fields,
            methods,
        };
    }
}
