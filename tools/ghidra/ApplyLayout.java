// Types GameAssembly.dll from the game's own IL2CPP layout (the mod's GET /il2cpp/layout,
// _il2cpp-layout.json): a struct per class with every field at its runtime offset
// (inherited ones included), and every method created, named Class$$method and given its
// signature (Class* __this first for instance methods, MethodInfo* last), so the
// decompiler shows this->metaSpawn->randomOffset instead of *(int *)(param_1 + 0x2c).
//
// Headless: analyzeHeadless <project dir> <name> -process GameAssembly.dll -noanalysis
//           -scriptPath tools/ghidra -postScript ApplyLayout.java <_il2cpp-layout.json>
// (tools/decomp.py --setup / --apply-layout run it.)
//@category JSAB

import java.io.FileReader;
import java.util.*;

import com.google.gson.*;

import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.listing.Function.FunctionUpdateType;
import ghidra.program.model.symbol.SourceType;

public class ApplyLayout extends GhidraScript {
    private static final CategoryPath CAT = new CategoryPath("/il2cpp");
    private final Map<String, JsonObject> classes = new HashMap<>();
    private final Map<String, Structure> structs = new HashMap<>();
    private DataTypeManager dtm;
    private final DataType voidPtr = new PointerDataType(VoidDataType.dataType);

    @Override
    public void run() throws Exception {
        String path = getScriptArgs().length > 0 ? getScriptArgs()[0] : askFile("Layout", "Open").getAbsolutePath();
        JsonObject root = JsonParser.parseReader(new FileReader(path)).getAsJsonObject();
        dtm = currentProgram.getDataTypeManager();
        for (JsonElement e : root.getAsJsonArray("classes")) {
            JsonObject c = e.getAsJsonObject();
            classes.put(c.get("name").getAsString(), c);
        }

        int tx = currentProgram.startTransaction("il2cpp layout");
        try {
            // Pass 1: a struct per class (empty, right size) so fields can point at any class.
            for (JsonObject c : classes.values()) {
                if (c.get("enum").getAsBoolean()) continue;
                String name = c.get("name").getAsString();
                int size = Math.max(1, c.get("size").getAsInt());
                Structure s = new StructureDataType(CAT, typeName(name), size, dtm);
                structs.put(name, (Structure) dtm.resolve(s, DataTypeConflictHandler.REPLACE_HANDLER));
            }
            // Pass 2: fields, own and inherited, at their runtime offsets.
            int fieldCount = 0;
            for (JsonObject c : classes.values()) {
                if (c.get("enum").getAsBoolean()) continue;
                Structure s = structs.get(c.get("name").getAsString());
                boolean value = c.get("valueType").getAsBoolean();
                if (!value) {
                    put(s, 0, voidPtr, "klass");
                    put(s, 8, voidPtr, "monitor");
                }
                for (JsonObject k = c; k != null; k = k.get("parent").isJsonNull() ? null : classes.get(k.get("parent").getAsString())) {
                    for (JsonElement fe : k.getAsJsonArray("fields")) {
                        JsonObject f = fe.getAsJsonObject();
                        if (f.get("static").getAsBoolean()) continue;
                        int off = f.get("offset").getAsInt() - (value ? 0x10 : 0);
                        if (put(s, off, fieldType(f.get("type").getAsString(), f.get("kind").getAsInt()), f.get("name").getAsString())) fieldCount++;
                    }
                    if (value) break;   // value types don't inherit fields
                }
            }
            // Static fields: Class__StaticFields (what klass->static_fields, +0xB8, points at).
            for (JsonObject c : classes.values()) {
                int max = 0;
                for (JsonElement fe : c.getAsJsonArray("fields")) {
                    JsonObject f = fe.getAsJsonObject();
                    if (f.get("static").getAsBoolean()) max = Math.max(max, f.get("offset").getAsInt() + 8);
                }
                if (max == 0) continue;
                Structure st = new StructureDataType(CAT, typeName(c.get("name").getAsString()) + "__StaticFields", max, dtm);
                for (JsonElement fe : c.getAsJsonArray("fields")) {
                    JsonObject f = fe.getAsJsonObject();
                    if (f.get("static").getAsBoolean())
                        put(st, f.get("offset").getAsInt(), fieldType(f.get("type").getAsString(), f.get("kind").getAsInt()), f.get("name").getAsString());
                }
                dtm.resolve(st, DataTypeConflictHandler.REPLACE_HANDLER);
            }
            println("structs: " + structs.size() + ", fields placed: " + fieldCount);

            // Methods: create, name, type.
            FunctionManager fm = currentProgram.getFunctionManager();
            Address base = currentProgram.getImageBase();
            Set<Address> done = new HashSet<>();
            Map<String, Integer> seen = new HashMap<>();
            int created = 0, named = 0, typed = 0;
            for (JsonObject c : classes.values()) {
                String cls = c.get("name").getAsString();
                for (JsonElement me : c.getAsJsonArray("methods")) {
                    JsonObject m = me.getAsJsonObject();
                    long rva = m.get("rva").getAsLong();
                    if (rva <= 0) continue;
                    Address a = base.add(rva);
                    if (!done.add(a)) continue;   // shared code (empty stubs, generic sharing): first owner names it
                    Function fn = fm.getFunctionAt(a);
                    if (fn == null) {
                        new DisassembleCommand(a, null, true).applyTo(currentProgram, monitor);
                        CreateFunctionCmd cmd = new CreateFunctionCmd(a);
                        if (!cmd.applyTo(currentProgram, monitor)) continue;
                        fn = fm.getFunctionAt(a);
                        if (fn == null) continue;
                        created++;
                    }
                    String name = typeName(cls) + "$$" + m.get("name").getAsString().replace('.', '_');
                    int n = seen.merge(name, 1, Integer::sum);
                    if (n > 1) name += "_" + n;
                    try { fn.setName(name, SourceType.IMPORTED); named++; } catch (Exception ex) { /* keep the old name */ }
                    try { if (setSignature(fn, c, m)) typed++; } catch (Exception ex) { /* keep Ghidra's */ }
                }
            }
            println("functions created: " + created + ", named: " + named + ", typed: " + typed);
        } finally {
            currentProgram.endTransaction(tx, true);
        }
    }

    private boolean setSignature(Function fn, JsonObject c, JsonObject m) throws Exception {
        List<Variable> params = new ArrayList<>();
        if (!m.get("static").getAsBoolean()) {
            Structure s = structs.get(c.get("name").getAsString());
            params.add(new ParameterImpl("__this", s == null ? voidPtr : new PointerDataType(s), currentProgram));
        }
        Set<String> names = new HashSet<>(Collections.singleton("__this"));
        int i = 0;
        for (JsonElement pe : m.getAsJsonArray("params")) {
            JsonObject p = pe.getAsJsonObject();
            String pn = p.get("name").isJsonNull() ? "p" + i : p.get("name").getAsString();
            if (!names.add(pn)) pn = pn + "_" + i;
            params.add(new ParameterImpl(pn, paramType(p.get("type").getAsString(), p.get("kind").getAsInt()), currentProgram));
            i++;
        }
        params.add(new ParameterImpl("method", voidPtr, currentProgram));
        JsonObject r = m.getAsJsonObject("ret");
        DataType rt = r.get("kind").getAsInt() == 1 ? VoidDataType.dataType : paramType(r.get("type").getAsString(), r.get("kind").getAsInt());
        fn.updateFunction("__fastcall", new ReturnParameterImpl(rt, currentProgram), params,
            FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS, true, SourceType.IMPORTED);
        return true;
    }

    // Field types: primitives by IL2CPP type code; classes as pointers to their struct; value
    // types of known size embedded; enums as their underlying integer.
    private DataType fieldType(String type, int kind) {
        DataType prim = primitive(kind);
        if (prim != null) return prim;
        String bare = bare(type);
        JsonObject c = classes.get(bare);
        if (kind == 0x11) {   // value type
            if (c == null) return null;   // unknown size: leave undefined
            if (c.get("enum").getAsBoolean()) return enumBase(c);
            Structure s = structs.get(bare);
            return s;
        }
        Structure s = structs.get(bare);
        return s == null ? voidPtr : new PointerDataType(s);
    }

    // Parameters: like fields, but value types too big for a register go by pointer.
    private DataType paramType(String type, int kind) {
        DataType t = fieldType(type, kind);
        if (t == null) return voidPtr;
        if (t instanceof Structure && t.getLength() > 8) return new PointerDataType(t);
        return t;
    }

    private DataType enumBase(JsonObject c) {
        for (JsonElement fe : c.getAsJsonArray("fields")) {
            JsonObject f = fe.getAsJsonObject();
            if (!f.get("static").getAsBoolean()) {
                DataType p = primitive(f.get("kind").getAsInt());
                if (p != null) return p;
            }
        }
        return IntegerDataType.dataType;
    }

    private static DataType primitive(int kind) {
        switch (kind) {
            case 0x02: return BooleanDataType.dataType;
            case 0x03: return UnsignedShortDataType.dataType;   // char
            case 0x04: return SignedByteDataType.dataType;
            case 0x05: return ByteDataType.dataType;
            case 0x06: return ShortDataType.dataType;
            case 0x07: return UnsignedShortDataType.dataType;
            case 0x08: return IntegerDataType.dataType;
            case 0x09: return UnsignedIntegerDataType.dataType;
            case 0x0a: return LongLongDataType.dataType;
            case 0x0b: return UnsignedLongLongDataType.dataType;
            case 0x0c: return FloatDataType.dataType;
            case 0x0d: return DoubleDataType.dataType;
            case 0x18: case 0x19: return new PointerDataType(VoidDataType.dataType);
            default: return null;
        }
    }

    // "System.Collections.Generic.List<Foo>" -> "System.Collections.Generic.List"; arrays -> element.
    private static String bare(String type) {
        int lt = type.indexOf('<');
        if (lt >= 0) type = type.substring(0, lt);
        while (type.endsWith("[]")) type = type.substring(0, type.length() - 2);
        return type;
    }

    private static String typeName(String name) {
        return name.replace('.', '_').replace('/', '$');
    }

    private boolean put(Structure s, int off, DataType dt, String name) {
        if (s == null || dt == null || off < 0) return false;
        int len = dt.getLength();
        if (len <= 0 || off + len > s.getLength()) return false;
        try {
            s.replaceAtOffset(off, dt, len, name, null);
            return true;
        } catch (Exception e) {
            return false;
        }
    }
}
