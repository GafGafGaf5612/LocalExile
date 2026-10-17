// Usage: -postScript Callers.java <outfile> <symbolName|hexAddr> [...]
// For each symbol (all symbols with that name, e.g. an import and its thunks) or address, lists the functions that
// reference it, with the referencing instruction addresses.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class Callers extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        SymbolTable st = currentProgram.getSymbolTable();
        for (int i = 1; i < a.length; i++) {
            List<Address> targets = new ArrayList<>();
            if (a[i].startsWith("0x")) targets.add(toAddr(a[i]));
            else for (Symbol s : st.getSymbols(a[i])) targets.add(s.getAddress());
            // Follow thunks to the import.
            for (Address t : new ArrayList<>(targets)) {
                Function f = getFunctionAt(t);
                if (f != null) for (Address th : f.getFunctionThunkAddresses(true) == null ? new Address[0] : f.getFunctionThunkAddresses(true)) targets.add(th);
            }
            out.println("== " + a[i] + " targets=" + targets);
            Map<String, List<String>> by = new TreeMap<>();
            for (Address t : targets) {
                for (Reference r : getReferencesTo(t)) {
                    Function c = getFunctionContaining(r.getFromAddress());
                    String k = c == null ? "?" : c.getEntryPoint() + " " + c.getName();
                    by.computeIfAbsent(k, x -> new ArrayList<>()).add(r.getFromAddress().toString());
                }
            }
            for (var e : by.entrySet()) out.println(e.getKey() + " @" + e.getValue());
        }
        out.close();
    }
}
