// Reads names (one per line, matched as suffix of symbol full name e.g. "WS2_32.DLL::send") or addresses (0x...) from
// Tools/re/out/rx.txt; writes the distinct functions that reference each into Tools/re/out/refs.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class Refs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String dir = "C:/Users/Frost/Desktop/Projects/Game/Tools/re/out/";
        PrintWriter out = new PrintWriter(new FileWriter(dir + "refs.txt"));
        for (String line : Files.readAllLines(Paths.get(dir + "rx.txt"))) {
            line = line.trim();
            if (line.isEmpty()) continue;
            List<Address> targets = new ArrayList<>();
            if (line.startsWith("0x")) targets.add(toAddr(line.substring(2)));
            else {
                SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
                while (it.hasNext()) { Symbol s = it.next(); if (s.getName(true).endsWith(line)) targets.add(s.getAddress()); }
            }
            out.println("## " + line + " targets=" + targets);
            Set<String> seen = new TreeSet<>();
            for (Address t : targets) {
                for (Reference r : getReferencesTo(t)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    seen.add((f == null ? "(none)" : f.getEntryPoint() + " " + f.getName()) + "  <- " + r.getFromAddress());
                }
            }
            for (String s : seen) out.println(s);
        }
        out.close();
    }
}
