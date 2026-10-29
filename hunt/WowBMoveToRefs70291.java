// WowBMoveToRefs70291.java -- bounded static pseudo-disassembly for build 70291.
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

public class WowBMoveToRefs70291 extends GhidraScript {
    private static final int MAX_INSTRUCTIONS = 45;
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
    // Selected raw RIP-reference sites; not function entries or callable APIs.
    private static final Seed[] SEEDS = {
        new Seed("MoveTo string reference SITE (not entry)", 0x1844575L),
        new Seed("MoveToRel string reference SITE (not entry)", 0x17A092L),
        new Seed("MoveToAbs string reference SITE (not entry)", 0x17A1D2L)
    };
    private static final long[] STRING_HITS = {
        0x4F6EC70L, 0x5123360L, 0x50E3398L, 0x50E33B8L
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
        Path file = dir.resolve("WowBMoveToRefs70291-" + stamp + ".txt");
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
                emitPredecessorCandidates(seed.rva);
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
            out("\n=== RAW POINTER PATTERN SEARCH (STATIC LEADS ONLY) ===");
            out("Searches preferred-image VA64 and RVA32 little-endian patterns. Hits can be coincidences or non-code metadata.");
            out("Incomplete relocations, relative references, encryption and scan caps can hide real users.");
            for (long hit : STRING_HITS) {
                if (monitor.isCancelled()) break;
                println("Searching pointer patterns near string " + rv(hit) + "...");
                rawStringRefs(hit);
                log.flush();
            }
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
    private byte[] littleEndian(long value, int length) {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++) result[i] = (byte)(value >>> (8 * i));
        return result;
    }
    private void rawStringRefs(long hitRva) {
        Address hit = toAddr(base + hitRva);
        MemoryBlock block = currentProgram.getMemory().getBlock(hit);
        if (block == null || !block.isInitialized() || !block.isRead()) {
            out("  SKIP unreadable hit " + rv(hitRva)); return;
        }
        try {
            Address start = hit;
            int back = 0;
            while (back < 96 && start.compareTo(block.getStart()) > 0) {
                Address prev = start.subtract(1);
                int c = currentProgram.getMemory().getByte(prev) & 0xFF;
                if (c < 0x20 || c > 0x7E) break;
                start = prev;
                back++;
            }
            out("\nstring hit=" + rv(hitRva) + " inferred printable start=" + addressRva(start) +
                " (bounded, not proof of string identity)");
            out("  bytes from start=" + hexAt(start, 80));
            emitStringNeighborhood(start, block);
            scanPointerPattern(start, false);
            scanPointerPattern(start, true);
            if (!start.equals(hit)) {
                scanPointerPattern(hit, false);
                scanPointerPattern(hit, true);
            }
        } catch (Exception e) { out("  string inspection error: " + e); }
    }
    private void emitStringNeighborhood(Address center, MemoryBlock block) {
        try {
            int before = (int)Math.min(96L, center.subtract(block.getStart()));
            Address first = center.subtract(before);
            int length = (int)Math.min(256L, block.getEnd().subtract(first) + 1);
            byte[] bytes = new byte[length];
            int got = currentProgram.getMemory().getBytes(first, bytes);
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < got; i++) {
                int c = bytes[i] & 255;
                text.append(c >= 32 && c <= 126 ? (char)c : '.');
            }
            out("  nearby printable bytes start=" + addressRva(first) + " [" + text + "] (not a verified table)");
        } catch (Exception e) { out("  neighborhood unavailable: " + e); }
    }
    private void emitPredecessorCandidates(long siteRva) {
        final int lookback = 48, minInstructions = 4, maxCandidates = 3;
        Address site = toAddr(base + siteRva);
        out("  Preceding raw bytes (48)=" + hexAt(site.subtract(lookback), lookback));
        out("  Alternative linear predecessor decodings, not proven boundaries or reachable flow:");
        int found = 0;
        for (int back = lookback; back >= 1 && found < maxCandidates && !monitor.isCancelled(); back--) {
            Address start = site.subtract(back), p = start;
            StringBuilder lines = new StringBuilder();
            int n = 0;
            try {
                while (p.compareTo(site) < 0 && n < 24 && executable(p)) {
                    Instruction ins = pseudo.disassemble(p);
                    if (ins == null || ins.getLength() <= 0 || p.add(ins.getLength()).compareTo(site) > 0 ||
                        ins.getFlowType().isTerminal() || (ins.getFlowType().isJump() && !ins.getFlowType().isConditional())) break;
                    lines.append("      ").append(addressRva(p)).append(" ").append(hexAt(p, ins.getLength()))
                         .append("  ").append(ins).append("\n");
                    p = p.add(ins.getLength()); n++;
                }
                if (p.equals(site) && n >= minInstructions) {
                    out("    candidate start=" + addressRva(start) + " instructions=" + n);
                    out(lines.toString()); found++;
                }
            } catch (Exception e) { /* candidate failure is expected on arbitrary starts */ }
        }
        if (found == 0) out("    no candidate meeting bounds; no negative semantic conclusion");
        if (found == maxCandidates) out("    additional possible boundaries omitted");
    }
    private void scanPointerPattern(Address target, boolean rva32) {
        long value = rva32 ? target.getOffset() - base : target.getOffset();
        byte[] bytes = littleEndian(value, rva32 ? 4 : 8);
        out("  scan target=" + addressRva(target) + " encoding=" +
            (rva32 ? "RVA32-LE" : "preferred-VA64-LE") + " cap=6");
        int found = 0;
        try {
            for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
                if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
                Address cursor = block.getStart();
                Address end = block.getEnd();
                while (cursor != null && cursor.compareTo(end) <= 0 && found < 6 && !monitor.isCancelled()) {
                    Address match = currentProgram.getMemory().findBytes(cursor, end, bytes, null, true, monitor);
                    if (match == null) break;
                    out("    raw-match=" + addressRva(match) + " block=" + block.getName() +
                        " executable=" + block.isExecute());
                    ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(match);
                    int n = 0;
                    while (refs.hasNext() && n < 4 && !monitor.isCancelled()) {
                        Reference ref = refs.next();
                        ghidra.program.model.listing.Function f = getFunctionContaining(ref.getFromAddress());
                        out("      existing-xref-from=" + addressRva(ref.getFromAddress()) +
                            " type=" + ref.getReferenceType() + " function=" +
                            (f == null ? "<none>" : f.getName() + "@" + addressRva(f.getEntryPoint())));
                        n++;
                    }
                    if (n == 0) out("      no existing exact xrefs to raw-match");
                    if (refs.hasNext()) out("      xrefs capped at 4");
                    found++;
                    if (match.equals(end)) break;
                    cursor = match.add(1);
                }
                if (found >= 6) break;
            }
            if (found == 0) out("    no raw matches in initialized readable blocks");
            if (found >= 6) out("    match cap reached; additional matches may exist");
        } catch (Exception e) { out("    pointer search error: " + e); }
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
