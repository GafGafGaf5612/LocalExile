// Usage: -postScript FindHtons.java <outfile> <hexId> [<hexId>...]
// Lists every function that calls htons with one of the given immediates in ecx/cx (candidate senders of a message id),
// with the call sites and the function's callers.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class FindHtons extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<Long> want = new HashSet<>();
        for (int i = 1; i < a.length; i++) want.add(Long.decode(a[i]));
        Listing l = currentProgram.getListing();
        for (Function f : l.getFunctions(true)) {
            List<String> hits = new ArrayList<>();
            long lastEcx = -1;
            for (Instruction ins : l.getInstructions(f.getBody(), true)) {
                String m = ins.getMnemonicString();
                if (m.equals("MOV") && ins.getNumOperands() == 2 && ins.getDefaultOperandRepresentation(0).matches("(?i)ecx|cx")) {
                    Object[] s = ins.getOpObjects(1);
                    lastEcx = (s.length == 1 && s[0] instanceof Scalar) ? ((Scalar) s[0]).getUnsignedValue() : -1;
                } else if (m.equals("CALL")) {
                    Reference[] rs = ins.getReferencesFrom();
                    Symbol sym = rs.length > 0 ? getSymbolAt(rs[0].getToAddress()) : null;
                    String name = sym == null ? "" : sym.getName();
                    if (name.contains("htons") && want.contains(lastEcx)) hits.add("0x" + Long.toHexString(lastEcx) + "@" + ins.getAddress());
                    lastEcx = -1;
                }
            }
            if (hits.isEmpty()) continue;
            List<String> callers = new ArrayList<>();
            for (Function c : f.getCallingFunctions(monitor)) callers.add(c.getEntryPoint().toString());
            out.println(f.getEntryPoint() + " " + f.getName() + " hits=" + hits + " callers=" + callers);
        }
        out.close();
    }
}
