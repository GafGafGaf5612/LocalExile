// Reads addresses (0x...) from Tools/re/out/rx.txt; writes "addr function" lines into Tools/re/out/containing.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.*;
import java.nio.file.*;

public class Containing extends GhidraScript {
    @Override
    public void run() throws Exception {
        String dir = "C:/Users/Frost/Desktop/Projects/Game/Tools/re/out/";
        PrintWriter out = new PrintWriter(new FileWriter(dir + "containing.txt"));
        for (String line : Files.readAllLines(Paths.get(dir + "rx.txt"))) {
            line = line.trim();
            if (!line.startsWith("0x")) continue;
            Function f = getFunctionContaining(toAddr(line.substring(2)));
            out.println(line + " " + (f == null ? "(none)" : f.getEntryPoint() + " " + f.getName()));
        }
        out.close();
    }
}
