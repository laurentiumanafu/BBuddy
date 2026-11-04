// WowBContainerCode70707.java -- bounded static container code follow-up for build 70707.
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

public class WowBContainerCode70707 extends GhidraScript {
    private static final int MAX_INSTRUCTIONS = 200;
    private static final int MAX_BYTES_FROM_ENTRY = 0x3000;
    private static final int MAX_BLOCKS = 32;
    private static final int MAX_XREFS_PER_ADDRESS = 8;

    // Exact RIP-relative LEA sites reported by the previous raw scan.
    // The site itself is not necessarily a function entry or a handler.
    private static final long[] SITES = { 0xD5C2E2L, 0xD5C82BL, 0xD65BE5L, 0xD65C04L };
    private static final String[] SITE_NAMES = { "free-slots local", "slot-count local", "free-slots registration candidate", "slot-count registration candidate" };
    private static final String[] SITE_BYTES = { "4C8D05A7173A03", "4C8D059E123A03", "4C8D05A47E3903", "4C8D05C57E3903" };
    private static final int MAX_PDATA_MATCHES = 4;
    private static final int MAX_FORWARD_CONTEXT = 28;
    private PrintWriter log;
    private PseudoDisassembler pseudo;
    private long base;

    @Override
    public void run() throws Exception {
        if (currentProgram == null) { println("No open program."); return; }
        Path dir = Paths.get("C:\\ghidra-scripts");
        Files.createDirectories(dir);
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS"));
        Path file = dir.resolve("WowBContainerCode70707-" + stamp + ".txt");
        BufferedWriter writer = Files.newBufferedWriter(file, StandardCharsets.UTF_8,
            StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        log = new PrintWriter(writer);
        base = currentProgram.getImageBase().getOffset();
        pseudo = new PseudoDisassembler(currentProgram);
        boolean complete = false;
        println("Writing read-only pseudo-disassembly to " + file);
        try {
            out("WOWB 70707 BOUNDED STATIC CONTAINER CODE FOLLOW-UP");
            out("program=" + currentProgram.getName() + " imageBase=" + currentProgram.getImageBase() +
                " language=" + currentProgram.getLanguageID());
            out("Pseudo-disassembly from known seeds; it does not modify the Ghidra listing.");
            out("Addresses below are RVAs, not runtime absolute addresses. Flow is deliberately truncated.");
            out("Instructions, target guesses and prior xrefs are unverified static leads, not callable APIs.");
            // Four prior 70707 static markers are a sanity check, not a full
            // hash-based image identification. Fail closed on any mismatch.
            long[] marks = { 0x2080D2CL, 0x20AAF97L, 0x2890305L, 0xA538FDL };
            String[] expected = { "488D0DCD38A004E808A843FE8B93541E0000", "F2480F2A83281E0000",
                "448B8C86C01B0000", "488D0D5CB32E06488D34C1488B04C1" };
            boolean markersMatch = true;
            for (int i = 0; i < marks.length; i++) {
                String actual = hexAt(toAddr(base + marks[i]), expected[i].length() / 2);
                boolean match = expected[i].equals(actual);
                out("70707 marker " + rv(marks[i]) + " " + (match ? "MATCH" : "MISMATCH OR UNREADABLE") + " bytes=" + actual);
                markersMatch &= match;
            }
            out("GetInventoryItemID table candidate RVA 0x492EDC0 raw 32 bytes=" +
                hexAt(toAddr(base + 0x492EDC0L), 32));
            out("Handler entry RVA 0x26CAA90 raw 16 bytes=" + hexAt(toAddr(base + 0x26CAA90L), 16));
            if (!markersMatch) {
                out("Image markers did not all match; refusing to trace this as build 70707.");
                return;
            }
            out("Markers match, but are not a complete image identity check. Direct calls listed as leads only.");
            for (int i = 0; i < SITES.length && !monitor.isCancelled(); i++) {
                out("\n=== " + SITE_NAMES[i] + " site=" + rv(SITES[i]) + " ===");
                String bytes = hexAt(toAddr(base + SITES[i]), SITE_BYTES[i].length() / 2);
                if (!bytes.equals(SITE_BYTES[i])) {
                    out("SITE MARKER MISMATCH OR UNREADABLE: " + bytes + "; skipped");
                    continue;
                }
                out("site marker MATCH bytes=" + bytes);
                emitSiteContext(SITES[i]);
                long entry = findPdataEntry(SITES[i]);
                if (i < 2 && entry != 0) {
                    out("--- bounded trace of candidate containing function, not a callable API ---");
                    trace(entry, MAX_INSTRUCTIONS, MAX_BYTES_FROM_ENTRY, new java.util.LinkedHashSet<>());
                }
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

    // Context starts at a known byte-pattern match. The backwards path is
    // heuristic; exact rejoining does not prove the instruction boundary.
    private void emitSiteContext(long siteRva) {
        Address site = toAddr(base + siteRva);
        out("  raw site-64..site+96=" + hexAt(site.subtract(64), 160));
        java.util.List<String> best = new java.util.ArrayList<>();
        for (int back = 64; back >= 1 && best.isEmpty(); back--) {
            Address p = site.subtract(back);
            java.util.List<String> path = new java.util.ArrayList<>();
            try {
                while (p.compareTo(site) < 0 && path.size() < 32 && executable(p)) {
                    Instruction ins = pseudo.disassemble(p);
                    if (ins == null || ins.getLength() <= 0) break;
                    path.add("    " + addressRva(p) + " " + hexAt(p, ins.getLength()) + "  " + ins);
                    p = p.add(ins.getLength());
                }
                if (p.equals(site)) best = path;
            } catch (Exception ignored) { }
        }
        out("  one heuristic linear path into site (may be misaligned or unreachable):");
        if (best.isEmpty()) out("    none found");
        else for (String line : best) out(line);
        out("  linear decode from exact site (branches not followed; alignment not proven):");
        Address p = site;
        for (int n = 0; n < MAX_FORWARD_CONTEXT && executable(p) && !monitor.isCancelled(); n++) {
            try {
                Instruction ins = pseudo.disassemble(p);
                if (ins == null || ins.getLength() <= 0) break;
                out("    " + addressRva(p) + " " + hexAt(p, ins.getLength()) + "  " + ins);
                p = p.add(ins.getLength());
            } catch (Exception e) { out("    decode stopped: " + e); break; }
        }
    }

    // x64 Windows PE runtime-function entries are triples of 32-bit RVAs.
    // Search initialized .pdata blocks only. A matched range is a static
    // lead, not a proof of correct function identity or reachability.
    private long findPdataEntry(long site) {
        int blocks = 0, matches = 0, failed = 0;
        long selected = 0;
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.getName().toLowerCase(java.util.Locale.ROOT).contains(".pdata") ||
                !block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
            blocks++;
            try {
                final int CHUNK = 12 * 8192;
                long size = block.getSize();
                if (size > 32L * 1024L * 1024L) { out("  .pdata block too large; skipped"); failed++; continue; }
                for (long off = 0; off + 12 <= size && !monitor.isCancelled(); off += CHUNK) {
                    int length = (int)Math.min(CHUNK, size - off);
                    length -= length % 12;
                    if (length == 0) break;
                    byte[] data = new byte[length];
                    int got = currentProgram.getMemory().getBytes(block.getStart().add(off), data);
                    if (got != length) { out("  .pdata partial read at " + rv(off)); failed++; break; }
                    for (int i = 0; i < length; i += 12) {
                        long begin = u32(data, i), end = u32(data, i + 4), unwind = u32(data, i + 8);
                        if (begin <= site && site < end && end > begin && executable(toAddr(base + begin))) {
                            matches++;
                            if (matches <= MAX_PDATA_MATCHES)
                                out("  pdata candidate begin=" + rv(begin) + " end=" + rv(end) + " unwind=" + rv(unwind) +
                                    " block=" + block.getName() + " (not proof of handler identity)");
                            selected = begin;
                        }
                    }
                }
            } catch (Exception e) { failed++; out("  .pdata read error: " + e); }
        }
        ghidra.program.model.listing.Function function = getFunctionContaining(toAddr(base + site));
        out("  existing Ghidra function=" + (function == null ? "none" : function.getName() + "@" + addressRva(function.getEntryPoint())));
        out("  pdata scan blocks=" + blocks + " matches=" + matches + " failed=" + failed +
            (matches > MAX_PDATA_MATCHES ? " (output capped)" : ""));
        if (matches == 1 && failed == 0) return selected;
        out("  no unique complete .pdata entry; candidate function trace omitted");
        return 0;
    }
    private long u32(byte[] data, int i) {
        return ((long)data[i] & 0xffL) | (((long)data[i + 1] & 0xffL) << 8) |
            (((long)data[i + 2] & 0xffL) << 16) | (((long)data[i + 3] & 0xffL) << 24);
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
    private void emitEntryXrefs(long rva) {
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(toAddr(base + rva));
        int count = 0;
        out("  Existing exact entry xrefs (up to " + MAX_XREFS_PER_ADDRESS + "; may be missing or incorrect):");
        while (refs.hasNext() && count < MAX_XREFS_PER_ADDRESS && !monitor.isCancelled()) {
            Reference ref = refs.next();
            out("    from=" + addressRva(ref.getFromAddress()) + " type=" + ref.getReferenceType());
            count++;
        }
        if (count == 0) out("    none");
    }
}
