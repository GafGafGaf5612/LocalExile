// Decompiles every function whose entry lies in [lo,hi]. Range in Tools/re/out/range.txt ("lo hi" hex), output Tools/re/out/range.c
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.nio.file.*;

public class Range extends GhidraScript {
    @Override
    public void run() throws Exception {
        String dir = "C:/Users/Frost/Desktop/Projects/Game/Tools/re/out/";
        String[] r = new String(Files.readAllBytes(Paths.get(dir + "range.txt"))).trim().split("\\s+");
        PrintWriter out = new PrintWriter(new FileWriter(dir + "range.c"));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        AddressSet set = new AddressSet(toAddr(r[0]), toAddr(r[1]));
        for (Function f : currentProgram.getFunctionManager().getFunctions(set, true)) {
            DecompileResults res = di.decompileFunction(f, 60, monitor);
            out.println("\n// ===== " + f.getName() + " @ " + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " =====");
            out.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// failed");
        }
        out.close();
    }
}
