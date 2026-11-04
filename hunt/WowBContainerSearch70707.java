// WowBContainerSearch70707.java -- bounded static container registration search for build 70707.
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

public class WowBContainerSearch70707 extends GhidraScript {
    private static final int MAX_INSTRUCTIONS = 100;
    private static final int MAX_BYTES_FROM_ENTRY = 0x1000;
    private static final int MAX_BLOCKS = 24;
    private static final int MAX_XREFS_PER_ADDRESS = 8;

    private static final String[] NAMES = {
        "GetInventoryItemID", // known positive control, do not trace it again
        "GetContainerNumSlots", "GetContainerNumFreeSlots", "GetContainerItemInfo"
    };
    private static final int MAX_NAMES_PER_STRING = 4;
    private static final int MAX_POINTER_REFS_PER_NAME = 8;
    private static final int MAX_TRACES_PER_NAME = 2;
    private PrintWriter log;
    private PseudoDisassembler pseudo;
    private long base;

    @Override
    public void run() throws Exception {
        if (currentProgram == null) { println("No open program."); return; }
        Path dir = Paths.get("C:\\ghidra-scripts");
        Files.createDirectories(dir);
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS"));
        Path file = dir.resolve("WowBContainerSearch70707-" + stamp + ".txt");
        BufferedWriter writer = Files.newBufferedWriter(file, StandardCharsets.UTF_8,
            StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        log = new PrintWriter(writer);
        base = currentProgram.getImageBase().getOffset();
        pseudo = new PseudoDisassembler(currentProgram);
        boolean complete = false;
        println("Writing read-only pseudo-disassembly to " + file);
        try {
            out("WOWB 70707 BOUNDED CONTAINER REGISTRATION SEARCH");
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
            for (String name : NAMES) {
                if (monitor.isCancelled()) break;
                println("Searching static registrations for " + name + "...");
                searchName(name);
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

    // Search exact ASCII names (including NUL), then search the raw image for
    // little-endian pointers to those names. Ghidra xrefs may be absent in a
    // no-analysis project, so do not rely solely on its reference database.
    private void searchName(String name) {
        out("\n=== name " + name + " (exact ASCII + NUL) ===");
        byte[] pattern = (name + "\0").getBytes(StandardCharsets.US_ASCII);
        int found = 0;
        Set<Long> traced = new HashSet<>();
        try {
            for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
                if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
                Address cursor = block.getStart(), end = block.getEnd();
                while (cursor.compareTo(end) <= 0 && found < MAX_NAMES_PER_STRING && !monitor.isCancelled()) {
                    Address hit = currentProgram.getMemory().findBytes(cursor, end, pattern, null, true, monitor);
                    if (hit == null) break;
                    out("  string=" + addressRva(hit) + " block=" + block.getName());
                    emitEntryXrefs(hit.getOffset() - base);
                    searchPointer(hit, name, traced);
                    // Some images use a fully qualified C_Container.Name string.
                    byte[] prefix = "C_Container.".getBytes(StandardCharsets.US_ASCII);
                    if (hit.subtract(block.getStart()) >= prefix.length) {
                        Address prefixed = hit.subtract(prefix.length);
                        if (hexAt(prefixed, prefix.length).equals("435F436F6E7461696E65722E")) {
                            out("  qualified name starts=" + addressRva(prefixed));
                            searchPointer(prefixed, name, traced);
                        }
                    }
                    found++;
                    if (hit.equals(end)) break;
                    cursor = hit.add(1);
                }
                if (found >= MAX_NAMES_PER_STRING) break;
            }
            if (found == 0) out("  no exact string found; this does not prove the handler is absent");
            if (found >= MAX_NAMES_PER_STRING) out("  string hit cap reached");
        } catch (Exception e) { out("  name search failed: " + e); }
    }

    private void searchPointer(Address nameAddress, String name, Set<Long> traced) {
        long value = nameAddress.getOffset();
        byte[] ptr = new byte[8];
        for (int i = 0; i < 8; i++) ptr[i] = (byte)(value >>> (i * 8));
        int found = 0;
        try {
            for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
                if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
                Address cursor = block.getStart(), end = block.getEnd();
                while (cursor.compareTo(end) <= 0 && found < MAX_POINTER_REFS_PER_NAME && !monitor.isCancelled()) {
                    Address hit = currentProgram.getMemory().findBytes(cursor, end, ptr, null, true, monitor);
                    if (hit == null) break;
                    found++;
                    out("    pointer reference=" + addressRva(hit) + " block=" + block.getName() +
                        " next16=" + hexAt(hit, 16));
                    byte[] next = new byte[8];
                    int read = 0;
                    try { read = currentProgram.getMemory().getBytes(hit.add(8), next); }
                    catch (Exception e) { out("      following qword unreadable: " + e.getClass().getSimpleName()); }
                    if (read == 8) {
                        long candidate = 0;
                        for (int i = 0; i < 8; i++) candidate |= (next[i] & 0xFFL) << (8 * i);
                        Address target = toAddr(candidate);
                        boolean code = executable(target);
                        out("      following qword=" + addressRva(target) + " executable=" + code +
                            " (candidate registration, not proof of semantics)");
                        if (code && !name.equals("GetInventoryItemID") && traced.size() < MAX_TRACES_PER_NAME && traced.add(candidate)) {
                            out("      bounded candidate trace (not a callable API):");
                            trace(candidate - base, MAX_INSTRUCTIONS, MAX_BYTES_FROM_ENTRY, new java.util.LinkedHashSet<>());
                        }
                    }
                    if (hit.equals(end)) break;
                    cursor = hit.add(1);
                }
                if (found >= MAX_POINTER_REFS_PER_NAME) break;
            }
            if (found == 0) out("    no direct pointer references; registration may use another encoding");
            if (found >= MAX_POINTER_REFS_PER_NAME) out("    pointer reference cap reached");
        } catch (Exception e) { out("    pointer search failed: " + e); }
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
