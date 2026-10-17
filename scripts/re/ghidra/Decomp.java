// Usage: analyzeHeadless <projdir> <proj> -process PathOfExile.exe -noanalysis -readOnly -scriptPath <this dir> -postScript Decomp.java <outfile> <addr|addr:depth> ...
// Decompiles the function containing each address; with ":N" also decompiles callees to depth N (unique).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class Decomp extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Long> seen = new HashSet<>();
        for (int i = 1; i < a.length; i++) {
            String[] p = a[i].split(":");
            Address ad = toAddr(p[0]);
            int depth = p.length > 1 ? Integer.parseInt(p[1]) : 0;
            Function f = getFunctionContaining(ad);
            if (f == null) { out.println("// no function at " + p[0]); continue; }
            dump(di, f, depth, seen, out);
        }
        out.close();
    }

    private void dump(DecompInterface di, Function f, int depth, Set<Long> seen, PrintWriter out) throws Exception {
        if (!seen.add(f.getEntryPoint().getOffset())) return;
        DecompileResults r = di.decompileFunction(f, 120, monitor);
        out.println("\n// ===== " + f.getName() + " @ " + f.getEntryPoint() + " =====");
        if (r.decompileCompleted()) out.println(r.getDecompiledFunction().getC());
        else out.println("// decompile failed: " + r.getErrorMessage());
        if (depth > 0)
            for (Function c : f.getCalledFunctions(monitor))
                if (!c.isThunk() && c.getBody().getNumAddresses() < 6000) dump(di, c, depth - 1, seen, out);
    }
}
