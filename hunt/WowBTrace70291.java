// WowBTrace70291.java -- bounded static pseudo-disassembly for build 70291.
// Read-only: does not change the program, attach to a process, or invoke code.
// @category WowB

import java.io.BufferedWriter;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardOpenOption;
import java.time.LocalDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayDeque;
import java.util.HashSet;
import java.util.Set;

import ghidra.app.script.GhidraScript;
import ghidra.app.util.PseudoDisassembler;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.FlowType;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class WowBTrace70291 extends GhidraScript {
    private static final int MAX_INSTRUCTIONS = 180;
    private static final int MAX_BYTES_FROM_ENTRY = 0x800;
    private static final int MAX_BLOCKS = 48;
    private static final int MAX_CALL_TARGETS = 3;
    private static final int MAX_CALLEE_INSTRUCTIONS = 65;
    private static final int MAX_CALLEE_BYTES = 0x300;
    private static final int MAX_XREFS_PER_ADDRESS = 8;

    private static final class Seed {
        final String name;
        final long rva;
        Seed(String name, long rva) { this.name = name; this.rva = rva; }
    }
    private static final Seed[] SEEDS = {
        new Seed("UnitHealth",       0x1D63190L),
        new Seed("UnitHealthMax",    0x1D616C0L),
        new Seed("UnitPower",        0x1D82590L),
        new Seed("UnitPowerMax",     0x1D84050L),
        new Seed("UnitReaction",     0x1D89A00L),
        new Seed("UnitCanAttack",    0x1D48E20L),
        new Seed("UnitIsEnemy",      0x1D6D260L),
        new Seed("UnitIsDead",       0x1D6C740L),
        new Seed("IsLoggedIn",       0x18E0180L),
        new Seed("TargetUnit",       0x1B46C60L),
        new Seed("StartAttack",      0x18EF5F0L),
        new Seed("CanLootUnit",      0x18B5CF0L),
        new Seed("MoveForwardStart", 0x23C8BE0L)
    };
    private PrintWriter log;
    private PseudoDisassembler pseudo;
    private long base;
    private final Set<Long> tracedCallees = new HashSet<>();

    @Override
    public void run() throws Exception {
        if (currentProgram == null) { println("No open program."); return; }
        Path dir = Paths.get("C:\\ghidra-scripts");
        Files.createDirectories(dir);
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS"));
        Path file = dir.resolve("WowBTrace70291-" + stamp + ".txt");
        BufferedWriter writer = Files.newBufferedWriter(file, StandardCharsets.UTF_8,
            StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        log = new PrintWriter(writer);
        base = currentProgram.getImageBase().getOffset();
        pseudo = new PseudoDisassembler(currentProgram);
        boolean complete = false;
        println("Writing read-only pseudo-disassembly to " + file);
        try {
            out("WOWB 70291 BOUNDED STATIC TRACE");
            out("program=" + currentProgram.getName() + " imageBase=" + currentProgram.getImageBase() +
                " language=" + currentProgram.getLanguageID());
            out("Pseudo-disassembly from known seeds; it does not modify the Ghidra listing.");
            out("Addresses below are RVAs, not runtime absolute addresses. Flow is deliberately truncated.");
            out("Instructions, target guesses and prior xrefs are unverified static leads, not callable APIs.");
            out("Prior UnitHealth 16-byte observation=488BC4554157488D68A14881ECB80000; observed=" +
                hexAt(toAddr(base + 0x1D63190L), 16));
            for (Seed seed : SEEDS) {
                if (monitor.isCancelled()) break;
                println("Tracing " + seed.name + "...");
                out("\n=== " + seed.name + " entry RVA=" + rv(seed.rva) + " ===");
                Set<Long> calls = new java.util.LinkedHashSet<>();
                trace(seed.rva, MAX_INSTRUCTIONS, MAX_BYTES_FROM_ENTRY, calls);
                int count = 0;
                for (long call : calls) {
                    if (count >= MAX_CALL_TARGETS || monitor.isCancelled()) break;
                    if (!tracedCallees.add(call)) { out("  Callee " + rv(call) + " already shown"); continue; }
                    out("\n--- Bounded direct-callee lead " + rv(call) + " (not an ABI claim) ---");
                    trace(call, MAX_CALLEE_INSTRUCTIONS, MAX_CALLEE_BYTES, new java.util.LinkedHashSet<>());
                    count++;
                }
                log.flush();
            }
            out("\n=== PRIOR TARGET-GUID WRITE XREF ===");
            out("Prior Ghidra analysis labeled instruction RVA 0x7A8941 WRITE to target GUID high half.");
            out("Metadata may be wrong; decoding that address is not proof it starts a valid instruction.");
            oneInstruction(0x7A8941L);
            out("\n=== MOVEMENT STRING VICINITY ===");
            out("Earlier ASCII autoInteract substring hit RVA 0x516ED88; search nearby xref destinations.");
            out("Xrefs may be incomplete; substring alone does not identify CTM or a CVar.");
            scanXrefs(0x516ED88L, 64);
            complete = !monitor.isCancelled();
        }
        finally {
            out("\n=== " + (complete ? "COMPLETE (bounds apply)" : "PARTIAL / CANCELLED / ERROR") + " ===");
            log.flush();
            boolean failed = log.checkError();
            log.close();
            println((failed ? "WARNING: output write failed: " : "Trace saved: ") + file);
        }
    }

    private void out(String s) { log.println(s); }
    private String rv(long value) { return String.format("0x%X", value); }
    private String addressRva(Address a) {
        if (Long.compareUnsigned(a.getOffset(), base) < 0) return "outside-image:" + a;
        return rv(a.getOffset() - base);
    }
    private String hexAt(Address address, int size) {
        try {
            byte[] b = new byte[size];
            int n = currentProgram.getMemory().getBytes(address, b);
            StringBuilder s = new StringBuilder();
            for (int i = 0; i < n; i++) s.append(String.format("%02X", b[i] & 0xFF));
            return n == size ? s.toString() : "partial:" + s;
        } catch (Exception e) { return "unreadable:" + e.getClass().getSimpleName(); }
    }
    private boolean executable(Address a) {
        MemoryBlock block = currentProgram.getMemory().getBlock(a);
        return block != null && block.isInitialized() && block.isExecute();
    }
    private void oneInstruction(long rva) {
        Address a = toAddr(base + rva);
        try {
            Instruction i = pseudo.disassemble(a);
            if (i == null) { out("  no instruction decoded"); return; }
            out("  RVA=" + rv(rva) + " bytes=" + hexAt(a, i.getLength()) + " " + i);
        } catch (Exception e) { out("  decode failed: " + e); }
    }
    private void trace(long entryRva, int maxInstructions, int maxBytes, Set<Long> calls) {
        Address entry = toAddr(base + entryRva);
        if (!executable(entry)) { out("  SKIP: address not in initialized executable block"); return; }
        ArrayDeque<Address> queue = new ArrayDeque<>();
        Set<Long> visited = new HashSet<>();
        queue.add(entry);
        int instructions = 0, blocks = 0;
        while (!queue.isEmpty() && instructions < maxInstructions && blocks < MAX_BLOCKS && !monitor.isCancelled()) {
            Address p = queue.removeFirst();
            if (visited.contains(p.getOffset())) continue;
            blocks++;
            out("  BLOCK " + addressRva(p));
            while (instructions < maxInstructions && !monitor.isCancelled()) {
                long delta = p.getOffset() - entry.getOffset();
                if (delta < 0 || delta >= maxBytes || !executable(p) || !visited.add(p.getOffset())) break;
                try {
                    Instruction ins = pseudo.disassemble(p);
                    if (ins == null) { out("    no instruction decoded at " + addressRva(p)); break; }
                    int length = ins.getLength();
                    if (length <= 0 || delta + length > maxBytes) { out("    instruction exceeds bound"); break; }
                    out("    " + addressRva(p) + " " + hexAt(p, length) + "  " + ins);
                    instructions++;
                    FlowType flow = ins.getFlowType();
                    Address[] targets = ins.getDefaultFlows();
                    if (flow.isCall()) {
                        for (Address target : targets) {
                            if (target != null && executable(target) && Long.compareUnsigned(target.getOffset(), base) >= 0) {
                                long rva = target.getOffset() - base;
                                calls.add(rva);
                                out("      direct call lead " + rv(rva));
                            }
                        }
                    }
                    if (flow.isJump()) {
                        for (Address target : targets) {
                            long d = target.getOffset() - entry.getOffset();
                            if (d >= 0 && d < maxBytes && executable(target) && !visited.contains(target.getOffset())) {
                                queue.add(target);
                                out("      queued local branch " + addressRva(target));
                            } else out("      branch out of bounds or already visited: " + addressRva(target));
                        }
                        if (!flow.isConditional()) break;
                    }
                    if (flow.isTerminal()) break;
                    p = p.add(length);
                } catch (Exception e) {
                    out("    decode stopped at " + addressRva(p) + ": " + e.getClass().getSimpleName() + " " + e.getMessage());
                    break;
                }
            }
        }
        out("  STOP instructions=" + instructions + "/" + maxInstructions + " blocks=" + blocks + "/" + MAX_BLOCKS +
            " pending=" + queue.size() + " entry-window=" + rv(maxBytes) + " bytes");
        out("  Linear/branch decoding can include invalid paths and can miss indirect or outside-window flow.");
    }
    private void scanXrefs(long centerRva, int radius) {
        for (long offset = -radius; offset <= radius && !monitor.isCancelled(); offset++) {
            Address target = toAddr(base + centerRva + offset);
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
            int n = 0;
            while (refs.hasNext() && n < MAX_XREFS_PER_ADDRESS) {
                Reference ref = refs.next();
                out("  to=" + addressRva(target) + " from=" + addressRva(ref.getFromAddress()) +
                    " type=" + ref.getReferenceType());
                n++;
            }
            if (refs.hasNext()) out("  ... more references omitted for " + addressRva(target));
        }
    }
}
