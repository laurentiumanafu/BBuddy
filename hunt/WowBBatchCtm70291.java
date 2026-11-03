// WowBBatchCtm70291.java -- bounded static pseudo-disassembly for build 70291.
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

public class WowBBatchCtm70291 extends GhidraScript {
    private static final int MAX_INSTRUCTIONS = 55;
    private static final int MAX_BYTES_FROM_ENTRY = 0x500;
    private static final int MAX_BLOCKS = 12;
    private static final int MAX_CALL_TARGETS = 0;
    private static final int MAX_CALLEE_INSTRUCTIONS = 65;
    private static final int MAX_CALLEE_BYTES = 0x300;
    private static final int MAX_XREFS_PER_ADDRESS = 8;

    private static final class Seed {
        final String name;
        final long rva;
        Seed(String name, long rva) { this.name = name; this.rva = rva; }
    }
    private static final Seed[] SEEDS = {};
    // Broader ASCII name search, not an exhaustive CTM search.
    private static final String[] CLICK_STRINGS = {
        "MoveTo", "ClickToMove", "ClickTo", "AutoMove", "ClickPosition",
        "ClickGround", "GroundClick", "TerrainClick", "ClickTerrain"
    };
    private final Set<Long> rawTargets = new java.util.LinkedHashSet<>();
    private final Set<Long> rawSites = new java.util.LinkedHashSet<>();
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
        Path file = dir.resolve("WowBBatchCtm70291-" + stamp + ".txt");
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
                out("\n=== " + seed.name + " seed RVA=" + rv(seed.rva) + " ===");
                Set<Long> calls = new java.util.LinkedHashSet<>();
                emitEntryXrefs(seed.rva);
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
            out("\n=== CLICK-TO-MOVE STRING LEADS ===");
            out("Case-sensitive ASCII substring matches only, not proof of a CVar, functionality, or CTM.");
            out("Substring hits and raw references are bounded; many references and other encodings are omitted.");
            for (String text : CLICK_STRINGS) {
                if (monitor.isCancelled()) break;
                println("Searching " + text + "...");
                searchClickText(text, false);
                log.flush();
            }
            out("\n=== SELECTED RAW RIP-RELATIVE REFERENCES ===");
            out("Subset of seven-byte REX.W LEA/MOV only; raw matches can be junk and missing refs prove nothing.");
            println("Scanning executable bytes for selected string reference candidates...");
            scanRipCandidates();
            out("\n=== BOUNDED AUTOMATIC SITE TRACES ===");
            out("First six raw sites in scan order; not function entries or proof of reachability.");
            int traced = 0;
            for (long site : rawSites) {
                if (traced >= 6 || monitor.isCancelled()) break;
                out("\n--- raw site " + rv(site) + " ---");
                trace(site, 16, 0x180, new java.util.LinkedHashSet<>());
                traced++;
            }
            out("  site traces=" + traced + " of " + rawSites.size() + " (cap 6)");
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
    private void searchClickText(String text, boolean utf16) {
        byte[] pattern = text.getBytes(StandardCharsets.US_ASCII);
        final int cap = text.equals("MoveTo") ? 16 : 5;
        int found = 0, failedBlocks = 0;
        out("\nSTRING " + text + " ASCII cap=" + cap);
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
            try {
                Address cursor = block.getStart(), end = block.getEnd();
                while (cursor.compareTo(end) <= 0 && found < cap && !monitor.isCancelled()) {
                    Address hit = currentProgram.getMemory().findBytes(cursor, end, pattern, null, true, monitor);
                    if (hit == null) break;
                    out("  hit=" + addressRva(hit) + " block=" + block.getName());
                    emitStringContext(hit, block, false);
                    found++;
                    if (hit.equals(end)) break;
                    cursor = hit.add(1);
                }
            } catch (Exception e) { failedBlocks++; out("  BLOCK ERROR " + block.getName() + " " + e); }
            if (found >= cap) break;
        }
        out("  found=" + found + " capped=" + (found >= cap) + " failedBlocks=" + failedBlocks);
    }
    private void emitStringContext(Address hit, MemoryBlock block, boolean utf16) {
        // Infer a printable prefix; it can be an interior substring or coincidental bytes.
        int step = utf16 ? 2 : 1;
        Address start = hit;
        try {
            for (int n = 0; n < 128; n++) {
                if (start.subtract(block.getStart()) < step) break;
                Address prev = start.subtract(step);
                int c = currentProgram.getMemory().getByte(prev) & 255;
                if (c < 32 || c > 126 || (utf16 && currentProgram.getMemory().getByte(prev.add(1)) != 0)) break;
                start = prev;
            }
            StringBuilder text = new StringBuilder();
            Address p = start;
            for (int n = 0; n < 96 && p.compareTo(block.getEnd()) <= 0; n++) {
                if (utf16 && p.add(1).compareTo(block.getEnd()) > 0) break;
                int c = currentProgram.getMemory().getByte(p) & 255;
                if (c < 32 || c > 126 || (utf16 && currentProgram.getMemory().getByte(p.add(1)) != 0)) break;
                text.append((char)c);
                p = p.add(step);
            }
            out("    inferred printable prefix start=" + addressRva(start) + " text=[" + text + "] (bounded, not validated)");
            rawTargets.add(hit.getOffset() - base);
            rawTargets.add(start.getOffset() - base);
        } catch (Exception e) { out("    printable context unavailable: " + e); }
    }
    private void scanRipCandidates() {
        final int CHUNK = 1024 * 1024;
        final int MAX_HITS = 24;
        int hits = 0, failedBlocks = 0;
        long positions = 0;
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isInitialized() || !block.isRead() || !block.isExecute()) continue;
            if (monitor.isCancelled()) break;
            out("  scan block=" + block.getName() + " start=" + addressRva(block.getStart()) +
                " size=" + block.getSize());
            Address cursor = block.getStart();
            try {
                while (cursor.compareTo(block.getEnd()) <= 0 && !monitor.isCancelled() && hits < MAX_HITS) {
                    long remaining = block.getEnd().subtract(cursor) + 1;
                    int requested = (int)Math.min((long)CHUNK + 6L, remaining);
                    if (requested < 7) break;
                    byte[] data = new byte[requested];
                    int got = currentProgram.getMemory().getBytes(cursor, data);
                    if (got != requested) {
                        out("    PARTIAL READ at " + addressRva(cursor) + " got=" + got + " expected=" + requested);
                        failedBlocks++; break;
                    }
                    int starts = Math.min(CHUNK, got - 6);
                    for (int i = 0; i < starts && hits < MAX_HITS; i++) {
                        positions++;
                        int rex = data[i] & 0xff, op = data[i + 1] & 0xff, modrm = data[i + 2] & 0xff;
                        if ((rex != 0x48 && rex != 0x4c) || (op != 0x8d && op != 0x8b && op != 0x89) ||
                            (modrm & 0xc7) != 0x05) continue;
                        int disp = (data[i + 3] & 0xff) | ((data[i + 4] & 0xff) << 8) |
                            ((data[i + 5] & 0xff) << 16) | ((data[i + 6] & 0xff) << 24);
                        long destination = cursor.getOffset() + i + 7L + (long)disp;
                        {
                            long target = destination - base;
                            if (!rawTargets.contains(target)) continue;
                            Address site = cursor.add(i);
                            ghidra.program.model.listing.Function f = getFunctionContaining(site);
                            out("    candidate site=" + addressRva(site) + " target=" + rv(target) +
                                " bytes=" + hexAt(site, 7) + " function=" +
                                (f == null ? "<none>" : f.getName() + "@" + addressRva(f.getEntryPoint())));
                            rawSites.add(site.getOffset() - base);
                            hits++;
                        }
                    }
                    if (remaining <= CHUNK) break;
                    cursor = cursor.add(CHUNK);
                }
            } catch (Exception e) {
                failedBlocks++;
                out("    BLOCK SCAN ERROR at " + addressRva(cursor) + ": " + e);
            }
            if (hits >= MAX_HITS) break;
        }
        out("  scan summary positions=" + positions + " candidates=" + hits + " failedBlocks=" + failedBlocks +
            " cancelled=" + monitor.isCancelled() + " capped=" + (hits >= MAX_HITS));
        out("  Only the listed encodings and targets were searched; pseudo-disassembly does not establish reachability.");
    }
    private void emitContextXrefs(long rva) {
        out("  context address RVA=" + rv(rva));
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(toAddr(base + rva));
        int count = 0;
        while (refs.hasNext() && count < 18 && !monitor.isCancelled()) {
            Reference ref = refs.next();
            ghidra.program.model.listing.Function f = getFunctionContaining(ref.getFromAddress());
            out("    from=" + addressRva(ref.getFromAddress()) + " type=" + ref.getReferenceType() +
                " function=" + (f == null ? "<none>" : f.getName() + "@" + addressRva(f.getEntryPoint())));
            count++;
        }
        if (count == 0) out("    no exact xrefs; indirect references may be missed");
        if (refs.hasNext()) out("    additional references omitted (cap 18)");
    }
    private void emitEntryXrefs(long rva) {
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(toAddr(base + rva));
        int count = 0;
        out("  Existing exact seed-address xrefs (up to 6; may be missing or incorrect):");
        while (refs.hasNext() && count < 6 && !monitor.isCancelled()) {
            Reference ref = refs.next();
            out("    from=" + addressRva(ref.getFromAddress()) + " type=" + ref.getReferenceType());
            count++;
        }
        if (count == 0) out("    none");
    }
}
