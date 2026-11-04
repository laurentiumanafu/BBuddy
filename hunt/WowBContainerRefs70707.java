// WowBContainerRefs70707.java -- bounded static container reference follow-up for build 70707.
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

import ghidra.app.script.GhidraScript;
import ghidra.app.util.PseudoDisassembler;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.MemoryBlock;

public class WowBContainerRefs70707 extends GhidraScript {

    private static final long[] TARGETS = { 0x4245E80L, 0x40FDAD0L, 0x40FDA90L, 0x40FDF88L };
    private static final String[] NAMES = { "GetInventoryItemID", "GetContainerNumSlots", "GetContainerNumFreeSlots", "GetContainerItemInfo" };
    private static final int MAX_RAW_MATCHES = 8;
    private static final int MAX_RIP_EXACT = 32;
    private static final int MAX_RIP_NEARBY = 32;
    private static final long NEAR_START = 0x40FD900L, NEAR_END = 0x40FE080L;
    private PrintWriter log;
    private PseudoDisassembler pseudo;
    private long base;

    @Override
    public void run() throws Exception {
        if (currentProgram == null) { println("No open program."); return; }
        Path dir = Paths.get("C:\\ghidra-scripts");
        Files.createDirectories(dir);
        String stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS"));
        Path file = dir.resolve("WowBContainerRefs70707-" + stamp + ".txt");
        BufferedWriter writer = Files.newBufferedWriter(file, StandardCharsets.UTF_8,
            StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        log = new PrintWriter(writer);
        base = currentProgram.getImageBase().getOffset();
        pseudo = new PseudoDisassembler(currentProgram);
        boolean complete = false;
        println("Writing read-only pseudo-disassembly to " + file);
        try {
            out("WOWB 70707 BOUNDED CONTAINER REFERENCE FOLLOW-UP");
            out("program=" + currentProgram.getName() + " imageBase=" + currentProgram.getImageBase() +
                " language=" + currentProgram.getLanguageID());
            out("Raw byte searches and pseudo-disassembly; does not modify the Ghidra listing.");
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
            boolean namesMatch = true;
            for (int i = 0; i < NAMES.length; i++) {
                byte[] expectedName = (NAMES[i] + "\0").getBytes(StandardCharsets.US_ASCII);
                StringBuilder expectedHex = new StringBuilder();
                for (byte x : expectedName) expectedHex.append(String.format("%02X", x & 0xff));
                String actual = hexAt(toAddr(base + TARGETS[i]), expectedName.length);
                boolean match = expectedHex.toString().equals(actual);
                out("name marker " + NAMES[i] + " " + rv(TARGETS[i]) + " " + (match ? "MATCH" : "MISMATCH OR UNREADABLE"));
                namesMatch &= match;
            }
            if (!namesMatch) { out("Name markers mismatch; no reference scan performed."); return; }
            dumpNameNeighborhood();
            for (int i = 0; i < NAMES.length && !monitor.isCancelled(); i++) {
                searchRvaBytes(TARGETS[i], NAMES[i]);
            }
            if (!monitor.isCancelled()) scanRipCandidates();
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

    // Region is fixed to the previous live Ghidra result. It is an
    // untrusted byte view, not evidence that these are native handlers.
    private void dumpNameNeighborhood() {
        out("\n=== printable ASCII runs in bounded container-name region ===");
        int length = (int)(NEAR_END - NEAR_START);
        try {
            byte[] data = new byte[length];
            int n = currentProgram.getMemory().getBytes(toAddr(base + NEAR_START), data);
            if (n != length) { out("partial region read: " + n + "/" + length); return; }
            int emitted = 0;
            for (int i = 0; i < data.length;) {
                int start = i;
                while (i < data.length && (data[i] & 0xff) >= 0x20 && (data[i] & 0xff) <= 0x7e) i++;
                if (i - start >= 5 && emitted < 160) {
                    out("  " + rv(NEAR_START + start) + " " + new String(data, start, i - start, StandardCharsets.US_ASCII));
                    emitted++;
                }
                if (i == start) i++;
            }
            out("  runs emitted=" + emitted + "; cap=160, non-printable bytes omitted");
        } catch (Exception e) { out("  region read failed: " + e); }
    }
    private void searchRvaBytes(long target, String name) {
        out("\n=== 32-bit little-endian RVA bytes " + name + " target=" + rv(target) + " ===");
        byte[] pattern = new byte[4];
        for (int i = 0; i < 4; i++) pattern[i] = (byte)(target >>> (8 * i));
        int hits = 0;
        try {
            for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
                if (!block.isInitialized() || !block.isRead() || monitor.isCancelled()) continue;
                Address cursor = block.getStart(), end = block.getEnd();
                while (cursor.compareTo(end) <= 0 && hits < MAX_RAW_MATCHES && !monitor.isCancelled()) {
                    Address hit = currentProgram.getMemory().findBytes(cursor, end, pattern, null, true, monitor);
                    if (hit == null) break;
                    out("  raw match=" + addressRva(hit) + " block=" + block.getName() +
                        " executable=" + block.isExecute() + " bytes=" + hexAt(hit, 24));
                    hits++;
                    if (hit.equals(end)) break;
                    cursor = hit.add(1);
                }
                if (hits >= MAX_RAW_MATCHES) break;
            }
        } catch (Exception e) { out("  RVA search failed: " + e); }
        out("  matches=" + hits + " cap=" + MAX_RAW_MATCHES + "; raw coincidences are not proof of references");
    }
    // Search executable bytes without relying on analysis. Decode only common
    // 64-bit RIP-relative MOV/LEA and non-REX LEA forms; bytes may be in the
    // middle of another instruction, so every result is only a candidate.
    private void scanRipCandidates() {
        final int CHUNK = 1024 * 1024;
        int exact = 0, nearby = 0, failed = 0;
        long positions = 0;
        out("\n=== raw RIP-relative candidate scan ===");
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isInitialized() || !block.isRead() || !block.isExecute() || monitor.isCancelled()) continue;
            Address cursor = block.getStart();
            try {
                while (cursor.compareTo(block.getEnd()) <= 0 && !monitor.isCancelled()) {
                    long remaining = block.getEnd().subtract(cursor) + 1;
                    int requested = (int)Math.min((long)CHUNK + 6, remaining);
                    if (requested < 6) break;
                    byte[] data = new byte[requested];
                    int got = currentProgram.getMemory().getBytes(cursor, data);
                    if (got != requested) { out("  partial executable block read at " + addressRva(cursor)); failed++; break; }
                    int starts = Math.min(CHUNK, got - 5);
                    for (int i = 0; i < starts; i++) {
                        int a = data[i] & 0xff, b = data[i + 1] & 0xff, c = data[i + 2] & 0xff;
                        int len, d;
                        if (i + 7 <= got && a >= 0x40 && a <= 0x4f && (b == 0x8d || b == 0x8b) && (c & 0xc7) == 0x05) { len = 7; d = i + 3; }
                        else if (a == 0x8d && (b & 0xc7) == 0x05) { len = 6; d = i + 2; }
                        else continue;
                        int displacement = (data[d] & 0xff) | ((data[d + 1] & 0xff) << 8) |
                            ((data[d + 2] & 0xff) << 16) | ((data[d + 3] & 0xff) << 24);
                        long rva = cursor.getOffset() + i + len + (long)displacement - base;
                        boolean isExact = false;
                        for (long t : TARGETS) if (rva == t) { isExact = true; break; }
                        boolean inRegion = rva >= NEAR_START && rva < NEAR_END;
                        if (!isExact && !inRegion) continue;
                        boolean show = isExact ? exact++ < MAX_RIP_EXACT : nearby++ < MAX_RIP_NEARBY;
                        if (!show) continue;
                        Address site = cursor.add(i);
                        String decoded;
                        try { Instruction ins = pseudo.disassemble(site); decoded = ins == null ? "<none>" : ins.toString(); }
                        catch (Exception e) { decoded = "error:" + e.getClass().getSimpleName(); }
                        out("  " + (isExact ? "exact" : "nearby") + " site=" + addressRva(site) + " destination=" + rv(rva) +
                            " bytes=" + hexAt(site, len) + " pseudo=" + decoded + " (unverified alignment)");
                    }
                    positions += starts;
                    if (remaining <= CHUNK) break;
                    cursor = cursor.add(CHUNK);
                }
            } catch (Exception e) { failed++; out("  executable block scan error: " + e); }
        }
        out("  scan summary positions=" + positions + " exact=" + exact + " nearby=" + nearby +
            " failedBlocks=" + failed + " cancelled=" + monitor.isCancelled() +
            " exactCap=" + MAX_RIP_EXACT + " nearbyCap=" + MAX_RIP_NEARBY);
        out("  Only selected RIP encodings were searched. Absence does not exclude references.");
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
}
