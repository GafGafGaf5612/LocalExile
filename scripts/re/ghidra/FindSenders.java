// Usage: -postScript FindSenders.java <outfile> <hexDisp> [<hexDisp>...]
// Lists every function whose code uses one of the given displacements (e.g. 0xdd0, the login socket field) together
// with the immediates passed to htons (candidate message ids) and the call targets, for mapping message senders.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class FindSenders extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<Long> disps = new HashSet<>();
        for (int i = 1; i < a.length; i++) disps.add(Long.decode(a[i]));
        Listing l = currentProgram.getListing();
        for (Function f : l.getFunctions(true)) {
            boolean hit = false;
            List<String> ids = new ArrayList<>();
            long lastEcx = -1;
            for (Instruction ins : l.getInstructions(f.getBody(), true)) {
                for (int op = 0; op < ins.getNumOperands(); op++) {
                    for (Object o : ins.getOpObjects(op)) {
                        if (o instanceof Scalar && disps.contains(((Scalar) o).getUnsignedValue())) hit = true;
                    }
                }
                String m = ins.getMnemonicString();
                if (m.equals("MOV") && ins.getNumOperands() == 2 && ins.getDefaultOperandRepresentation(0).matches("(?i)ecx|cx")) {
                    Object[] s = ins.getOpObjects(1);
                    lastEcx = (s.length == 1 && s[0] instanceof Scalar) ? ((Scalar) s[0]).getUnsignedValue() : -1;
                } else if (m.equals("CALL")) {
                    Reference[] rs = ins.getReferencesFrom();
                    String tgt = rs.length > 0 ? rs[0].getToAddress().toString() : "?";
                    Symbol sym = rs.length > 0 ? getSymbolAt(rs[0].getToAddress()) : null;
                    String name = sym == null ? tgt : sym.getName();
                    if (name.contains("htons") && lastEcx >= 0) ids.add("0x" + Long.toHexString(lastEcx) + "@" + ins.getAddress());
                    lastEcx = -1;
                }
            }
            if (hit) out.println(f.getEntryPoint() + " " + f.getName() + " ids=" + ids);
        }
        out.close();
    }
}
