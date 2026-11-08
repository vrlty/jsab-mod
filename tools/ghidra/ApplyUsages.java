// Names and types IL2CPP metadata-usage slots from the game (the mod's GET /il2cpp/usages,
// _il2cpp-usages.json): a class slot becomes <Class>_TypeInfo, typed so that its static
// fields read as <Class>_TypeInfo->static_fields->FIELD; methods <Class>$$name_MethodInfo;
// string literals str_<text>; types <name>_Type. Also names the IL2CPP runtime helpers
// every method calls. Run after ApplyLayout (it uses the __StaticFields structs).
//@category JSAB

import java.io.FileReader;
import java.util.*;

import com.google.gson.*;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;

public class ApplyUsages extends GhidraScript {
    private static final CategoryPath CAT = new CategoryPath("/il2cpp");
    // Runtime helpers (RVA -> name), identified from their use in decompiled game code.
    private static final Object[][] HELPERS = {
        { 0x219300L, "il2cpp_codegen_initialize_runtime_metadata" },
        { 0x229200L, "il2cpp_runtime_class_init" },
        { 0x260a50L, "ThrowNullReference" },
        { 0x14c7fe0L, "ThrowIndexOutOfRange" },
        { 0x2211f0L, "il2cpp_object_new" },
        { 0x21b660L, "il2cpp_resolve_icall" },
    };

    @Override
    public void run() throws Exception {
        String path = getScriptArgs()[0];
        JsonArray usages = JsonParser.parseReader(new FileReader(path)).getAsJsonArray();
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        SymbolTable st = currentProgram.getSymbolTable();
        Listing listing = currentProgram.getListing();
        Address base = currentProgram.getImageBase();
        DataType voidPtr = new PointerDataType(VoidDataType.dataType);

        int tx = currentProgram.startTransaction("il2cpp usages");
        try {
            for (Object[] h : HELPERS) {
                Function f = getFunctionAt(base.add((Long) h[0]));
                if (f != null) f.setName((String) h[1], SourceType.IMPORTED);
            }
            Map<String, Integer> seen = new HashMap<>();
            int n = 0;
            for (JsonElement e : usages) {
                JsonObject u = e.getAsJsonObject();
                Address a = base.add(u.get("rva").getAsLong());
                String kind = u.get("kind").getAsString(), name = u.get("name").getAsString();
                String label;
                DataType type = voidPtr;
                switch (kind) {
                    case "class": {
                        String cls = typeName(name);
                        label = cls + "_TypeInfo";
                        type = new PointerDataType(classStruct(dtm, cls));
                        break;
                    }
                    case "method": label = typeName(name.replace("::", "$$")) + "_MethodInfo"; break;
                    case "field": label = typeName(name.replace("::", "$$")) + "_FieldInfo"; break;
                    case "type": label = typeName(name) + "_Type"; break;
                    default: label = "str_" + strName(name); break;
                }
                int k = seen.merge(label, 1, Integer::sum);
                if (k > 1) label += "_" + k;
                try {
                    listing.clearCodeUnits(a, a.add(7), false);
                    listing.createData(a, type);
                    Symbol s = st.createLabel(a, label, SourceType.IMPORTED);
                    s.setPrimary();
                    if (kind.equals("string"))
                        listing.setComment(a, CodeUnit.EOL_COMMENT, name.length() > 200 ? name.substring(0, 200) + "..." : name);
                    n++;
                } catch (Exception ex) { /* leave this one */ }
            }
            println("usage slots named: " + n + " of " + usages.size());
        } finally {
            currentProgram.endTransaction(tx, true);
        }
    }

    // <Class>__Class: just enough of Il2CppClass to reach static_fields (+0xB8).
    private Structure classStruct(DataTypeManager dtm, String cls) {
        DataType existing = dtm.getDataType(CAT, cls + "__Class");
        if (existing instanceof Structure) return (Structure) existing;
        Structure s = new StructureDataType(CAT, cls + "__Class", 0x138, dtm);
        DataType statics = dtm.getDataType(CAT, cls + "__StaticFields");
        DataType sf = statics == null ? new PointerDataType(VoidDataType.dataType) : new PointerDataType(statics);
        s.replaceAtOffset(0xB8, sf, 8, "static_fields", null);
        return (Structure) dtm.resolve(s, DataTypeConflictHandler.REPLACE_HANDLER);
    }

    private static String typeName(String name) {
        return name.replace('.', '_').replace('/', '$').replaceAll("[^A-Za-z0-9_$]", "_");
    }

    private static String strName(String text) {
        String s = text.replaceAll("[^A-Za-z0-9]+", "_");
        if (s.length() > 32) s = s.substring(0, 32);
        return s.isEmpty() ? "empty" : s;
    }
}
