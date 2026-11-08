// Decompiles every method of the given classes (functions named Class$$method by
// ApplyLayout) into <out dir>/<Class>.c, one file per class, methods in address order.
//
// Headless: analyzeHeadless <project dir> <name> -process GameAssembly.dll -noanalysis
//           -readOnly -scriptPath tools/ghidra -postScript ExportClasses.java <out dir> Class...
// (tools/decomp.py runs it.) Class names as in the game ("RoundSpikes", "Outer/Inner").
//@category JSAB

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;

public class ExportClasses extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        File out = new File(args[0]);
        out.mkdirs();
        DecompInterface ifc = new DecompInterface();
        DecompileOptions opts = new DecompileOptions();
        ifc.setOptions(opts);
        ifc.openProgram(currentProgram);
        long base = currentProgram.getImageBase().getOffset();

        for (int i = 1; i < args.length; i++) {
            String cls = args[i], prefix = cls.replace('.', '_').replace('/', '$') + "$$";
            List<Function> fns = new ArrayList<>();
            for (Function f : currentProgram.getFunctionManager().getFunctions(true))
                if (f.getName().startsWith(prefix)) fns.add(f);
            StringBuilder sb = new StringBuilder();
            sb.append("// ").append(cls).append(": ").append(fns.size()).append(" methods, decompiled from GameAssembly.dll\n");
            for (Function f : fns) {
                long rva = f.getEntryPoint().getOffset() - base;
                sb.append("\n// ---- ").append(f.getName().substring(prefix.length()))
                  .append(String.format(" @ rva 0x%x", rva)).append('\n');
                DecompileResults r = ifc.decompileFunction(f, 120, monitor);
                if (r != null && r.decompileCompleted())
                    sb.append(r.getDecompiledFunction().getC());
                else
                    sb.append("// decompile failed: ").append(r == null ? "?" : r.getErrorMessage()).append('\n');
            }
            File file = new File(out, cls.replace('/', '$') + ".c");
            try (Writer w = new OutputStreamWriter(new FileOutputStream(file), StandardCharsets.UTF_8)) {
                w.write(sb.toString());
            }
            println("exported " + cls + ": " + fns.size() + " methods -> " + file);
        }
        ifc.dispose();
    }
}
