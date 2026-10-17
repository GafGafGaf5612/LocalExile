// Usage: -postScript RefsIn.java <outfile> <start:len> ...
// Lists every reference into [start, start+len) with the containing function of the referencing address.
// Use it to find who installs a vtable (len = vtable size) or touches a data blob.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class RefsIn extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (int i = 1; i < a.length; i++) {
            String[] p = a[i].split(":");
            Address s = toAddr(p[0]);
            long len = p.length > 1 ? Long.decode(p[1]) : 1;
            out.println("## " + a[i]);
            for (long k = 0; k < len; k++) {
                Address t = s.add(k);
                for (Reference r : rm.getReferencesTo(t)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    out.println("+" + Long.toHexString(k) + "  " + (f == null ? "(none)" : f.getEntryPoint() + " " + f.getName())
                        + "  <- " + r.getFromAddress() + " " + r.getReferenceType());
                }
            }
        }
        out.close();
    }
}
