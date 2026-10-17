// Usage: analyzeHeadless <projdir> <proj> -process PathOfExile.exe -noanalysis -readOnly -scriptPath <this dir>
//          -postScript DecompAll.java <outdir> [threads=4] [limit=0] [indexonly]
// Decompiles every non-thunk function to <outdir>/<hex digits 3-5 of the VA>/FUN_<addr>.c (e.g. 1e5/FUN_1e5123450.c)
// and writes <outdir>/index.tsv (addr, size, name, callers, status, strings referenced). Resumable: a function whose .c
// file exists is skipped (files are written to .tmp and renamed, so a crash leaves no partial file). Progress goes to
// <outdir>/progress.log every 1000 functions. limit > 0 stops after that many functions (for tests); "indexonly"
// rebuilds index.tsv without decompiling.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

public class DecompAll extends GhidraScript {
    static final int TIMEOUT = 60;
    static final String FAILED = "// decompile failed: ";

    private File outDir;
    private PrintWriter log;
    private final AtomicInteger done = new AtomicInteger(), ok = new AtomicInteger(), failed = new AtomicInteger();
    private final ConcurrentHashMap<Long, String> status = new ConcurrentHashMap<>();
    private long t0;
    private int todo;

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        outDir = new File(a[0]);
        int threads = a.length > 1 ? Integer.parseInt(a[1]) : 4;
        int limit = a.length > 2 ? Integer.parseInt(a[2]) : 0;
        boolean indexOnly = a.length > 3 && a[3].equals("indexonly");
        outDir.mkdirs();
        log = new PrintWriter(new FileWriter(new File(outDir, "progress.log"), true), true);

        List<Function> all = new ArrayList<>();
        int thunks = 0;
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            if (f.isExternal()) continue;
            if (f.isThunk()) { thunks++; continue; }
            all.add(f);
        }
        List<Function> work = new ArrayList<>();
        int skipped = 0;
        for (Function f : all) {
            if (limit > 0 && work.size() + skipped >= limit) break;
            if (fileFor(f).exists()) skipped++;
            else work.add(f);
        }
        todo = indexOnly ? 0 : work.size();
        log("start: " + all.size() + " non-thunk functions, " + thunks + " thunks; " + skipped
            + " already done, " + todo + " to decompile, " + threads + " threads");

        t0 = System.currentTimeMillis();
        if (!indexOnly) {
            ExecutorService pool = Executors.newFixedThreadPool(threads);
            ThreadLocal<DecompInterface> tl = new ThreadLocal<>();
            List<DecompInterface> ifaces = Collections.synchronizedList(new ArrayList<>());
            for (Function f : work) {
                pool.submit(() -> {
                    try {
                        if (monitor.isCancelled()) return;
                        DecompInterface di = tl.get();
                        if (di == null) { di = newIface(); tl.set(di); ifaces.add(di); }
                        if (!decompile(di, f)) { di.dispose(); ifaces.remove(di); tl.remove(); }
                    } catch (Throwable e) {
                        log("error " + f.getEntryPoint() + ": " + e);
                    }
                    int n = done.incrementAndGet();
                    if (n % 1000 == 0) progress(n);
                });
            }
            pool.shutdown();
            while (!pool.awaitTermination(60, TimeUnit.SECONDS)) {}
            for (DecompInterface di : ifaces) di.dispose();
            progress(done.get());
        }

        int[] st = writeIndex(limit > 0 ? all.subList(0, Math.min(limit, all.size())) : all);
        log("finished: decompiled " + ok.get() + ", failed " + failed.get() + " this run; skipped (already done) "
            + skipped + ". index.tsv: " + all.size() + " functions, " + st[0] + " ok, " + st[1] + " failed, "
            + st[2] + " missing; " + thunks + " thunks excluded. " + elapsed());
        log.close();
    }

    private DecompInterface newIface() {
        DecompInterface di = new DecompInterface();
        DecompileOptions o = new DecompileOptions();
        o.grabFromProgram(currentProgram);
        o.setMaxPayloadMBytes(200);
        di.setOptions(o);
        di.toggleCCode(true);
        di.toggleSyntaxTree(false);
        di.setSimplificationStyle("decompile");
        if (!di.openProgram(currentProgram)) throw new RuntimeException("openProgram: " + di.getLastMessage());
        return di;
    }

    /** Returns false when the decompiler process should be replaced (timeout or crash). */
    private boolean decompile(DecompInterface di, Function f) throws IOException {
        DecompileResults r = di.decompileFunction(f, TIMEOUT, ghidra.util.task.TaskMonitor.DUMMY);
        StringBuilder sb = new StringBuilder();
        sb.append("// ===== ").append(f.getName()).append(" @ ").append(f.getEntryPoint()).append(" =====\n");
        boolean good = r != null && r.decompileCompleted() && r.getDecompiledFunction() != null;
        String err = null;
        if (good) sb.append(r.getDecompiledFunction().getC());
        else {
            err = r == null ? "null result" : r.getErrorMessage().trim().replace('\n', ' ');
            if (r != null && r.isTimedOut()) err = "timeout " + TIMEOUT + " s; " + err;
            sb.append(FAILED).append(err).append('\n');
        }
        File out = fileFor(f);
        out.getParentFile().mkdirs();
        File tmp = new File(out.getPath() + ".tmp");
        Files.write(tmp.toPath(), sb.toString().getBytes(StandardCharsets.UTF_8));
        Files.move(tmp.toPath(), out.toPath(), StandardCopyOption.REPLACE_EXISTING);
        status.put(f.getEntryPoint().getOffset(), good ? "ok" : "failed");
        if (good) ok.incrementAndGet();
        else { failed.incrementAndGet(); log("failed " + f.getEntryPoint() + " (" + f.getBody().getNumAddresses() + " bytes): " + err); }
        return good || r == null || !(r.isTimedOut() || r.failedToStart() || !r.isValid());
    }

    private File fileFor(Function f) {
        String ad = f.getEntryPoint().toString();
        String dir = ad.length() >= 5 ? ad.substring(2, 5) : "low";
        return new File(new File(outDir, dir), "FUN_" + ad + ".c");
    }

    private int[] writeIndex(List<Function> all) throws Exception {
        int[] st = new int[3];
        File tmp = new File(outDir, "index.tsv.tmp");
        Listing listing = currentProgram.getListing();
        ReferenceManager rm = currentProgram.getReferenceManager();
        try (PrintWriter w = new PrintWriter(new OutputStreamWriter(new FileOutputStream(tmp), StandardCharsets.UTF_8))) {
            w.println("addr\tsize\tname\tcallers\tstatus\tstrings");
            int n = 0;
            for (Function f : all) {
                if (monitor.isCancelled()) break;
                String s = status.get(f.getEntryPoint().getOffset());
                if (s == null) s = statusFromFile(fileFor(f));
                st[s.equals("ok") ? 0 : s.equals("failed") ? 1 : 2]++;
                LinkedHashSet<String> strs = new LinkedHashSet<>();
                for (Instruction ins : listing.getInstructions(f.getBody(), true))
                    for (Reference ref : ins.getReferencesFrom()) {
                        if (!ref.getToAddress().isMemoryAddress()) continue;
                        String v = stringAt(listing, ref.getToAddress(), 1);
                        if (v != null && strs.size() < 200) strs.add(v);
                    }
                Set<Function> callers = f.getCallingFunctions(monitor);
                w.println("0x" + f.getEntryPoint() + "\t" + f.getBody().getNumAddresses() + "\t" + clean(f.getName())
                    + "\t" + callers.size() + "\t" + s + "\t" + String.join(" | ", strs));
                if (++n % 20000 == 0) log("index: " + n + "/" + all.size() + " " + elapsed());
            }
        }
        Files.move(tmp.toPath(), new File(outDir, "index.tsv").toPath(), StandardCopyOption.REPLACE_EXISTING);
        return st;
    }

    /** The string at an address, or behind one pointer to it. */
    private String stringAt(Listing listing, Address ad, int depth) {
        Data d = listing.getDataAt(ad);
        if (d == null) return null;
        if (d.hasStringValue()) {
            Object v = d.getValue();
            if (v == null) return null;
            String s = clean(v.toString());
            return s.length() > 300 ? s.substring(0, 300) + "…" : s;
        }
        if (depth > 0 && d.isPointer() && d.getValue() instanceof Address p && p.isMemoryAddress())
            return stringAt(listing, p, depth - 1);
        return null;
    }

    private static String clean(String s) {
        return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\r", "\\r").replace("\n", "\\n").replace(" | ", " \\| ");
    }

    private static String statusFromFile(File f) {
        if (!f.exists()) return "missing";
        try (BufferedReader r = Files.newBufferedReader(f.toPath(), StandardCharsets.UTF_8)) {
            r.readLine();
            String l = r.readLine();
            return l != null && l.startsWith(FAILED) ? "failed" : "ok";
        } catch (IOException e) { return "missing"; }
    }

    private void progress(int n) {
        double secs = (System.currentTimeMillis() - t0) / 1000.0;
        double rate = n / Math.max(secs, 0.001);
        long eta = rate > 0 ? (long) ((todo - n) / rate) : -1;
        Runtime rt = Runtime.getRuntime();
        log(String.format("%d/%d decompiled (%d ok, %d failed), %.1f/s, elapsed %s, ETA %s, heap %d/%d MB", n, todo,
            ok.get(), failed.get(), rate, hms((long) secs), hms(eta),
            (rt.totalMemory() - rt.freeMemory()) >> 20, rt.maxMemory() >> 20));
    }

    private String elapsed() { return "elapsed " + hms((System.currentTimeMillis() - t0) / 1000); }

    private static String hms(long s) { return s < 0 ? "?" : String.format("%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60); }

    private synchronized void log(String m) {
        String line = new java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss").format(new Date()) + " " + m;
        log.println(line);
        println(line);
    }
}
