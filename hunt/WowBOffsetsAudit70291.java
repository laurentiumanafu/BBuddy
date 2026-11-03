// WowBOffsetsAudit70291.java
// Read-only Ghidra analysis for WoW Classic Beta WowB build 70291.
// Writes a timestamped UTF-8 log under C:\ghidra-scripts\; no debugger,
// process access, game calls, patching, memory writes, or project changes.
//
// Open the 70291 DECODED image in the existing analyzed Ghidra project;
// Window -> Script Manager -> add C:\ghidra-scripts -> run this script.
// It can also run headless as a postScript. The output reports an observed
// byte signature but a match does not establish build identity or live state.
// Registration-derived RVAs are research leads, NOT proven callable functions.
// @category WowB

import java.io.BufferedWriter;
import java.io.IOException;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class WowBOffsetsAudit70291 extends GhidraScript {
    private static final int DECOMPILE_TIMEOUT_SECONDS = 20;
    private static final int MAX_C_LINES = 220;
    private static final int MAX_CALLS_PER_FUNCTION = 40;
    private static final int MAX_REFERENCES = 64;
    private static final int MAX_STRING_HITS = 4;
    private static final int MAX_DATA_FUNCTIONS = 4;
    private static final int MAX_EXTRA_DECOMPILES = 8;

    private static final class Seed {
        final String name;
        final long rva;
        Seed(String name, long rva) { this.name = name; this.rva = rva; }
    }

    // Lua handler RVAs from hunt/api_handlers.tsv; movement seeds are from
    // prior live/crash observations. A handler is not a native setter.
    private static final Seed[] SEEDS = {
        new Seed("UnitHealth",       0x1D63190L),
        new Seed("UnitHealthMax",    0x1D616C0L),
        new Seed("UnitPower",        0x1D82590L),
        new Seed("UnitPowerMax",     0x1D84050L),
        new Seed("UnitLevel",        0x1D7B330L),
        new Seed("UnitReaction",     0x1D89A00L),
        new Seed("UnitCanAttack",    0x1D48E20L),
        new Seed("UnitIsEnemy",      0x1D6D260L),
        new Seed("UnitIsDead",       0x1D6C740L),
        new Seed("CanLootUnit",      0x18B5CF0L),
        new Seed("IsLoggedIn",       0x18E0180L),
        new Seed("GetRealZoneText",  0x1E7BD90L),
        new Seed("TargetUnit",       0x1B46C60L),
        new Seed("StartAttack",      0x18EF5F0L),
        new Seed("MoveForwardStart", 0x23C8BE0L),
        new Seed("movement-stack-location (may be inside a function)", 0x23C550DL)
    };

    private static final Seed[] DATA_SEEDS = {
        new Seed("PlayerTargetGuid low",  0x7BDFF38L),
        new Seed("PlayerTargetGuid low+4", 0x7BDFF3CL),
        new Seed("PlayerTargetGuid high",  0x7BDFF40L),
        new Seed("PlayerTargetGuid high+4", 0x7BDFF44L),
        new Seed("PlayerGuid low",        0x61C72F0L),
        new Seed("PlayerGuid high",       0x61C72F8L),
        new Seed("ObjectManager global",  0x7B7B738L)
    };

    private static final String[] MOVEMENT_STRINGS = {
        "clickToMove", "ClickToMove", "enableClickToMove", "autoInteract"
    };

    private DecompInterface decomp;
    private PrintWriter log;
    private long imageBase;
    private final Set<Long> decompiled = new HashSet<>();
    private int extraDecompiles;

    @Override
    public void run() throws Exception {
        if (currentProgram == null) {
            println("No program is open. Open the analyzed 70291 decoded image first.");
            return;
        }
        Path directory = Paths.get("C:\\ghidra-scripts");
        Files.createDirectories(directory);
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS"));
        Path output = directory.resolve("WowBOffsetsAudit70291-" + stamp + ".txt");
        BufferedWriter buffered = Files.newBufferedWriter(output, StandardCharsets.UTF_8,
            StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        log = new PrintWriter(buffered);
        imageBase = currentProgram.getImageBase().getOffset();
        println("WowB offset audit: writing " + output);
        println("Read-only static analysis; do not run game code. This can take several minutes.");
        boolean finished = false;
        try {
            out("WOWB 70291 OFFSET AUDIT — READ ONLY");
            out("program=" + currentProgram.getName());
            out("imageBase=" + currentProgram.getImageBase() + " language=" + currentProgram.getLanguageID());
            out("All RVAs below are relative to the Ghidra program image base.");
            out("This report may be partial for encrypted, missing or unanalyzed code.");
            out("A function name/RVA is not a proven field offset, ABI or safe call.");
            out("No gameplay calls, process writes, breakpoints, or program edits are performed.");
            out("");
            signatureCheck();

            decomp = new DecompInterface();
            decomp.setOptions(new DecompileOptions());
            if (!decomp.openProgram(currentProgram)) {
                out("ERROR: decompiler could not open the program; report incomplete.");
                return;
            }

            out("\n=== API HANDLER AND MOVEMENT LEADS ===");
            for (Seed seed : SEEDS) {
                if (monitor.isCancelled()) break;
                println("Auditing " + seed.name + "...");
                auditSeed(seed);
                log.flush();
            }

            out("\n=== GUID/OBJECT-MANAGER DATA XREFS ===");
            for (Seed seed : DATA_SEEDS) {
                if (monitor.isCancelled()) break;
                println("Collecting xrefs for " + seed.name + "...");
                auditData(seed);
                log.flush();
            }

            out("\n=== MOVEMENT STRING LEADS ===");
            out("The actual CVar name is unknown. An absent string proves nothing.");
            for (String text : MOVEMENT_STRINGS) {
                if (monitor.isCancelled()) break;
                println("Searching movement string: " + text + "...");
                findString(text, false);
                if (!monitor.isCancelled()) findString(text, true);
                log.flush();
            }
            finished = !monitor.isCancelled();
        }
        finally {
            out("\n=== " + (finished ? "COMPLETE" : "PARTIAL / CANCELLED / ERROR") + " ===");
            out("Decompiled distinct functions: " + decompiled.size());
            out("Extra data-xref decompiles: " + extraDecompiles + " / " + MAX_EXTRA_DECOMPILES);
            if (decomp != null) decomp.dispose();
            log.flush();
            boolean failed = log.checkError();
            log.close();
            println((failed ? "WARNING: log write failed: " : "Offset audit saved: ") + output);
        }
    }

    private void out(String line) { log.println(line); }

    private Address rvaAddress(long rva) { return toAddr(imageBase + rva); }

    private String asRva(Address a) {
        long value = a.getOffset();
        if (Long.compareUnsigned(value, imageBase) < 0) return "outside-image:" + a;
        return String.format("0x%X", value - imageBase);
    }

    private String bytesAt(long rva, int count) {
        try {
            Address a = rvaAddress(rva);
            byte[] bytes = new byte[count];
            int got = currentProgram.getMemory().getBytes(a, bytes);
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < got; i++) result.append(String.format("%02X", bytes[i] & 0xFF));
            return got == count ? result.toString() : "partial:" + result;
        }
        catch (Exception e) { return "unreadable:" + e.getClass().getSimpleName(); }
    }

    private void signatureCheck() {
        String actual = bytesAt(0x1D63190L, 16);
        String reference = "488BC4554157488D68A14881ECB80000";
        out("UnitHealth RVA 0x1D63190 bytes=" + actual);
        out("Previously observed 16-byte signature=" + reference);
        if (!reference.equals(actual))
            out("WARNING: signature differs/unreadable. Wrong build, packed/cold bytes, or a different/partial image are possible; do not treat output as 70291 proof.");
        else
            out("Signature matches this one prior observation; this is NOT full build or runtime verification.");
    }

    private void auditSeed(Seed seed) {
        out("\n--- " + seed.name + " requested RVA=" + String.format("0x%X", seed.rva) + " ---");
        out("entry bytes=" + bytesAt(seed.rva, 16));
        Address address = rvaAddress(seed.rva);
        Function function = getFunctionAt(address);
        if (function == null) {
            function = getFunctionContaining(address);
            if (function != null)
                out("WARNING: requested address is inside " + function.getName() + " at " + asRva(function.getEntryPoint()) + "; not necessarily an entry point.");
        }
        if (function == null) {
            out("NO FUNCTION: Ghidra has no analyzed function at this location. No disassembly or analysis changes were made.");
            return;
        }
        decompile(function, "seed");
    }

    private void decompile(Function function, String reason) {
        long entry = function.getEntryPoint().getOffset();
        if (!decompiled.add(entry)) {
            out("  Already emitted function " + function.getName() + " @ " + asRva(function.getEntryPoint()));
            return;
        }
        out("FUNCTION " + function.getName() + " RVA=" + asRva(function.getEntryPoint()) +
            " bodyBytes=" + function.getBody().getNumAddresses() + " reason=" + reason);
        try {
            DecompileResults results = decomp.decompileFunction(function, DECOMPILE_TIMEOUT_SECONDS, monitor);
            if (results == null || !results.decompileCompleted() || results.getDecompiledFunction() == null) {
                out("DECOMPILE INCOMPLETE: " + (results == null ? "no result" : results.getErrorMessage()));
            }
            else {
                String[] lines = results.getDecompiledFunction().getC().split("\\n", -1);
                int limit = Math.min(lines.length, MAX_C_LINES);
                for (int i = 0; i < limit; i++) out(lines[i]);
                if (lines.length > limit) out("... TRUNCATED " + (lines.length - limit) + " lines");
            }
            List<Function> calls = new ArrayList<>(function.getCalledFunctions(monitor));
            calls.sort(Comparator.comparingLong(f -> f.getEntryPoint().getOffset()));
            out("CALLEES (up to " + MAX_CALLS_PER_FUNCTION + "; xrefs can be incomplete):");
            int n = 0;
            for (Function callee : calls) {
                if (monitor.isCancelled() || n >= MAX_CALLS_PER_FUNCTION) break;
                out("  " + callee.getName() + " RVA=" + asRva(callee.getEntryPoint()));
                n++;
            }
            if (calls.size() > n) out("  ... " + (calls.size() - n) + " more calls omitted");
        }
        catch (Exception e) { out("DECOMPILE/CALLEE ERROR: " + e); }
    }

    private void auditData(Seed seed) {
        Address address = rvaAddress(seed.rva);
        out("\n--- " + seed.name + " RVA=" + String.format("0x%X", seed.rva) + " ---");
        List<Function> writers = new ArrayList<>();
        List<Function> others = new ArrayList<>();
        Set<Long> seen = new HashSet<>();
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(address);
        int count = 0;
        while (refs.hasNext() && count < MAX_REFERENCES && !monitor.isCancelled()) {
            Reference reference = refs.next();
            Address from = reference.getFromAddress();
            Function function = getFunctionContaining(from);
            String type = reference.getReferenceType().toString();
            out("  from=" + asRva(from) + " type=" + type + " function=" +
                (function == null ? "<none>" : function.getName() + "@" + asRva(function.getEntryPoint())));
            if (function != null && seen.add(function.getEntryPoint().getOffset())) {
                if (type.toUpperCase(Locale.ROOT).contains("WRITE")) writers.add(function);
                else others.add(function);
            }
            count++;
        }
        if (count == 0) out("  No static references. Indirect/unresolved references can be absent.");
        if (refs.hasNext()) out("  ... references capped at " + MAX_REFERENCES);
        out("  Xref type is analysis metadata, not proof of a setter or data semantics.");
        writers.addAll(others);
        int selected = 0;
        for (Function function : writers) {
            if (selected >= MAX_DATA_FUNCTIONS || extraDecompiles >= MAX_EXTRA_DECOMPILES || monitor.isCancelled()) break;
            if (!decompiled.contains(function.getEntryPoint().getOffset())) {
                decompile(function, "data xref candidate");
                extraDecompiles++;
                selected++;
            }
        }
    }

    private void findString(String text, boolean utf16) {
        byte[] pattern = text.getBytes(utf16 ? StandardCharsets.UTF_16LE : StandardCharsets.US_ASCII);
        out("\nSTRING " + text + " encoding=" + (utf16 ? "UTF-16LE" : "ASCII") + " (substring match)");
        int found = 0;
        try {
            for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
                if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
                Address start = block.getStart();
                Address end = block.getEnd();
                while (start != null && start.compareTo(end) <= 0 && found < MAX_STRING_HITS && !monitor.isCancelled()) {
                    Address hit = currentProgram.getMemory().findBytes(start, end, pattern, null, true, monitor);
                    if (hit == null) break;
                    out("  match RVA=" + asRva(hit) + " block=" + block.getName());
                    ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(hit);
                    int count = 0;
                    while (refs.hasNext() && count < 12 && !monitor.isCancelled()) {
                        Reference reference = refs.next();
                        Function f = getFunctionContaining(reference.getFromAddress());
                        out("    xref from=" + asRva(reference.getFromAddress()) + " type=" + reference.getReferenceType() +
                            " function=" + (f == null ? "<none>" : f.getName() + "@" + asRva(f.getEntryPoint())));
                        count++;
                    }
                    if (count == 0) out("    no exact static xrefs (indirect/overlapping refs can be missed)");
                    found++;
                    if (hit.equals(end)) break;
                    start = hit.add(1);
                }
                if (found >= MAX_STRING_HITS) break;
            }
            if (found == 0) out("  no match in initialized readable blocks");
            if (found >= MAX_STRING_HITS) out("  hit cap reached; additional matches may exist");
        }
        catch (Exception e) { out("  STRING SEARCH ERROR: " + e); }
    }
}
