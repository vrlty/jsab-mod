// Lists IL2CPP metadata-usage slots: the globals a method hands to
// il2cpp_codegen_initialize_runtime_metadata (FUN_180219300) on first run, which the
// runtime then fills with a class, string literal, method or field. Their values in the
// file are encoded (usage type << 29 | index << 1 | 1); the mod's GET /il2cpp/usages
// resolves them in the running game, and ApplyUsages.java names and types them.
//
// Writes [{ "rva": slot - image base, "encoded": value in the file }] to the given path.
//@category JSAB

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.symbol.*;

public class ListUsageSlots extends GhidraScript {
    private static final long INIT_METADATA = 0x180219300L;

    @Override
    public void run() throws Exception {
        String outPath = getScriptArgs()[0];
        long base = currentProgram.getImageBase().getOffset();
        Address init = toAddr(INIT_METADATA);
        TreeMap<Long, Long> slots = new TreeMap<>();
        int calls = 0;
        for (Reference ref : getReferencesTo(init)) {
            if (!ref.getReferenceType().isCall()) continue;
            calls++;
            // The slot is loaded into RCX just before the call: LEA RCX,[slot].
            Instruction ins = getInstructionAt(ref.getFromAddress());
            for (int back = 0; back < 6 && ins != null; back++) {
                ins = ins.getPrevious();
                if (ins == null) break;
                if (!ins.getMnemonicString().equalsIgnoreCase("LEA")) continue;
                Object[] dst = ins.getOpObjects(0);
                if (dst.length != 1 || !(dst[0] instanceof Register) || !((Register) dst[0]).getName().equalsIgnoreCase("RCX")) continue;
                // RIP-relative: the operand resolves to the slot's address (references only
                // exist after analysis, so read the operand itself).
                Address slot = null;
                for (Object o : ins.getOpObjects(1))
                    if (o instanceof Address) slot = (Address) o;
                if (slot == null)
                    for (Reference r : ins.getReferencesFrom())
                        if (r.getToAddress().isMemoryAddress()) slot = r.getToAddress();
                if (slot == null) {
                    // Ghidra gives [RIP + disp] as the absolute address, as a scalar.
                    for (Object o : ins.getOpObjects(1))
                        if (o instanceof ghidra.program.model.scalar.Scalar)
                            slot = toAddr(((ghidra.program.model.scalar.Scalar) o).getUnsignedValue());
                }
                if (slot != null && currentProgram.getMemory().contains(slot))
                    slots.put(slot.getOffset() - base, currentProgram.getMemory().getLong(slot));
                break;
            }
        }
        try (Writer w = new OutputStreamWriter(new FileOutputStream(outPath), StandardCharsets.UTF_8)) {
            w.write("[");
            boolean first = true;
            for (Map.Entry<Long, Long> e : slots.entrySet()) {
                w.write((first ? "" : ",") + "{\"rva\":" + e.getKey() + ",\"encoded\":" + e.getValue() + "}");
                first = false;
            }
            w.write("]");
        }
        println("calls: " + calls + ", slots: " + slots.size() + " -> " + outPath);
    }
}
