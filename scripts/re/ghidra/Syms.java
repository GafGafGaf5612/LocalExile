// Usage: -postScript Syms.java <outfile> <regex>   lists symbols (incl. namespaces/classes) whose full name matches regex
import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.regex.*;

public class Syms extends GhidraScript {
    @Override
    public void run() throws Exception {
        // I/O via fixed files (shell quoting of args is unreliable): regex in rx.txt, results in syms.txt
        String dir = new java.io.File(getSourceFile().getAbsolutePath()).getParentFile().getParentFile()
            .getParentFile().getParentFile().getPath().replace('\\', '/') + "/Tools/re/out/";
        PrintWriter out = new PrintWriter(new FileWriter(dir + "syms.txt"));
        Pattern p = Pattern.compile(new String(java.nio.file.Files.readAllBytes(java.nio.file.Paths.get(dir + "rx.txt"))).trim(), Pattern.CASE_INSENSITIVE);
        SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
        int n = 0;
        while (it.hasNext() && !monitor.isCancelled()) {
            Symbol s = it.next();
            String name = s.getName(true).replaceFirst("^(.*)$", "$1");
            if (p.matcher(name).find()) { out.println(s.getAddress() + "\t" + s.getSymbolType() + "\t" + name); n++; }
        }
        out.println("// " + n + " matches");
        out.close();
    }
}
