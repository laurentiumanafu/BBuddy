// WowBHunt29.java - Ghidra 12.x static probe #7 for WowB.exe (1.60.1, build 70291)
//
// Live probes (hunt25..hunt28) already delivered the READ path:
//   pools -> records -> guid/pos/facing/flags, record+0xD8 -> unit data block
//   (health 0x1E30/0x1E38, power 0x1BC8, unit flags 0x1E60), lua_State global
//   confirmed at 0x7B76AB8. What is still missing is the EXECUTION path and
//   the NAME path. This script attacks both statically.
//
// Sections:
//   [T1] decompile FnApiRegister 0xAE96A0            -> where name/fn pairs land
//   [T2] decompile 0x271F2F (pool-record creation)   -> class A/B evidence
//   [T3] decompile 0x244466 (target GUID -> name)    -> the name cache
//   [T4] decompile 0x471A960 + 0x48A2140 (Lua C-API siblings)
//   [T5] xrefs to 0xAE96A0                           -> registration call sites
//   [T6] xrefs to 0x2BBB7F0 (FnEntityGetGuid)        -> who consumes GUIDs
//   [T7] xrefs to 0x27FBD0 (pool element accessor)   -> who reads records
//   [T8] STRING HUNT: RunScript / UnitHealth / UnitName / CastSpellByName /
//        MoveForwardStart / JumpOrAscendStart / TargetUnit / GetActionInfo
//        -> every occurrence, every xref, decompile up to 2 referencing
//        functions, then one extra hop: decompile up to 2 FUN_ addresses that
//        those functions reference (that is how the API IMPLEMENTATION is
//        reached), for RunScript/UnitHealth/UnitName.
//
// Run: Ghidra GUI -> Window -> Script Manager -> add C:\ghidra-scripts ->
//      run WowBHunt29 with the WowBLive project open, window = console.
//   (or headless: analyzeHeadless <proj> WowBLive -process WowB.exe
//    -scriptPath C:\ghidra-scripts -postScript WowBHunt29.java)
// NOTE: keep this file ALONE in its script folder for headless runs - the
// launcher compiles every .java it finds there.
//
// @category WowB

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class WowBHunt29 extends GhidraScript {

    private DecompInterface decomp;
    private long imageBase;

    @Override
    public void run() throws Exception {
        imageBase = currentProgram.getImageBase().getOffset();
        out("=== WowBHunt29 on " + currentProgram.getName()
                + "  imageBase=0x" + Long.toHexString(imageBase) + " ===");

        decomp = new DecompInterface();
        decomp.setOptions(new ghidra.app.decompiler.DecompileOptions());
        decomp.openProgram(currentProgram);

        // ---- [T1..T4] direct decompiles -------------------------------
        dec(0xAE96A0L, 90, "T1 FnApiRegister");
        dec(0x271F2FL, 120, "T2 pool-record creation path");
        dec(0x244466L, 120, "T3 target GUID -> name lookup");
        dec(0x471A960L, 60, "T4a Lua C-API sibling 0x471A960");
        dec(0x48A2140L, 60, "T4b Lua C-API sibling 0x48A2140");

        // ---- [T5..T7] xref overviews ----------------------------------
        xrefs(0xAE96A0L, 40, "T5 xrefs -> FnApiRegister");
        xrefs(0x2BBB7F0L, 40, "T6 xrefs -> FnEntityGetGuid");
        xrefs(0x27FBD0L, 40, "T7 xrefs -> pool element accessor");

        // ---- [T8] string hunt ----------------------------------------
        String[] names = {
            "RunScript", "UnitHealth", "UnitName", "CastSpellByName",
            "MoveForwardStart", "JumpOrAscendStart", "TargetUnit", "GetActionInfo"
        };
        for (String nm : names) {
            stringHunt(nm);
        }

        decomp.dispose();
        out("=== WowBHunt29 done ===");
    }

    // ------------------------------------------------------------------
    private void out(String s) {
        println(s);
    }

    private String rva(long va) {
        return "0x" + Long.toHexString(va - imageBase);
    }

    private void dec(long rvaAddr, int maxLines, String tag) {
        out("");
        out("---- " + tag + "  (VA 0x" + Long.toHexString(imageBase + rvaAddr)
                + " / RVA " + rva(imageBase + rvaAddr) + ") ----");
        try {
            Address a = toAddr(imageBase + rvaAddr);
            Function f = getFunctionContaining(a);
            if (f == null) {
                f = getFunctionAt(a);
            }
            if (f == null) {
                out("  [!] no function at that address (or not computed)");
                return;
            }
            out("  function " + f.getName() + " @ RVA " + rva(f.getEntryPoint().getOffset())
                    + "  size=" + f.getBody().getNumAddresses());
            DecompileResults res = decomp.decompileFunction(f, 90, monitor);
            if (res == null || !res.decompileCompleted()) {
                out("  [!] decompile failed");
                return;
            }
            String c = res.getDecompiledFunction().getC();
            String[] lines = c.split("\n", -1);
            int n = 0;
            for (String l : lines) {
                if (n >= maxLines) {
                    out("  ... (" + (lines.length - n) + " more lines)");
                    break;
                }
                out("  " + l);
                n++;
            }
        }
        catch (Exception e) {
            out("  [!] exception: " + e);
        }
    }

    private void xrefs(long rvaAddr, int max, String tag) {
        out("");
        out("---- " + tag + " ----");
        try {
            Address a = toAddr(imageBase + rvaAddr);
            ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(a);
            int n = 0;
            Set<String> fns = new LinkedHashSet<>();
            while (it.hasNext() && n < max) {
                Reference r = it.next();
                Address from = r.getFromAddress();
                Function f = getFunctionContaining(from);
                String fname = (f == null) ? "<no fn>" : f.getName();
                fns.add((f == null) ? "?" : rva(f.getEntryPoint().getOffset()));
                out("  from RVA " + rva(from.getOffset()) + " [" + fname + "] type=" + r.getReferenceType());
                n++;
            }
            if (n == 0) {
                out("  (no references)");
            }
            out("  distinct containing functions: " + fns.size());
        }
        catch (Exception e) {
            out("  [!] exception: " + e);
        }
    }

    private void stringHunt(String text) {
        out("");
        out("==== STRING HUNT '" + text + "' ====");
        try {
            byte[] pat = text.getBytes("US-ASCII");
            List<Address> found = new ArrayList<>();
            Address start = currentProgram.getMinAddress();
            while (start != null && found.size() < 2) {
                Address hit = currentProgram.getMemory().findBytes(start, currentProgram.getMaxAddress(), pat, null, true, monitor);
                if (hit == null) {
                    break;
                }
                found.add(hit);
                start = hit.add(1);
            }
            if (found.isEmpty()) {
                out("  string not present");
                return;
            }
            for (Address hit : found) {
                out("  string @ VA 0x" + hit + " / RVA " + rva(hit.getOffset()));
                ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(hit);
                int n = 0;
                Set<Long> fnAddrs = new LinkedHashSet<>();
                while (it.hasNext() && n < 6) {
                    Reference r = it.next();
                    Address from = r.getFromAddress();
                    Function f = getFunctionContaining(from);
                    out("    xref from RVA " + rva(from.getOffset()) + " ["
                            + ((f == null) ? "<no fn>" : f.getName() + " @" + rva(f.getEntryPoint().getOffset()))
                            + "] type=" + r.getReferenceType());
                    if (f != null) {
                        fnAddrs.add(f.getEntryPoint().getOffset());
                    }
                    n++;
                }
                if (n == 0) {
                    out("    (no xrefs)");
                }
                // decompile up to 2 referencing functions (the registration code)
                int d = 0;
                for (Long fa : fnAddrs) {
                    if (d >= 2) {
                        break;
                    }
                    Function f = getFunctionContaining(toAddr(fa));
                    if (f == null) {
                        continue;
                    }
                    out("    -- decompile registration fn " + f.getName() + " @ RVA " + rva(fa) + " --");
                    String c = decompileToString(f, 50);
                    out(c);
                    // two-hop: for the most informative APIs, decompile referenced FUN_ bodies too
                    if (text.equals("RunScript") || text.equals("UnitHealth") || text.equals("UnitName")) {
                        hop(c, 2, 45, "      ");
                    }
                    d++;
                }
            }
        }
        catch (Exception e) {
            out("  [!] exception: " + e);
        }
    }

    private String decompileToString(Function f, int maxLines) {
        StringBuilder sb = new StringBuilder();
        try {
            DecompileResults res = decomp.decompileFunction(f, 90, monitor);
            if (res == null || !res.decompileCompleted()) {
                return "      [!] decompile failed";
            }
            String[] lines = res.getDecompiledFunction().getC().split("\n", -1);
            int n = 0;
            for (String l : lines) {
                if (n >= maxLines) {
                    sb.append("      ... (").append(lines.length - n).append(" more lines)\n");
                    break;
                }
                sb.append("      ").append(l).append('\n');
                n++;
            }
        }
        catch (Exception e) {
            sb.append("      [!] ").append(e).append('\n');
        }
        return sb.toString();
    }

    /** Decompile up to <max> distinct FUN_xxxx targets referenced by the given C text. */
    private void hop(String cText, int max, int maxLines, String indent) {
        Pattern p = Pattern.compile("FUN_14([0-9a-fA-F]{7})");
        Matcher m = p.matcher(cText);
        Set<Long> seen = new LinkedHashSet<>();
        while (m.find() && seen.size() < max) {
            long va = Long.parseUnsignedLong("14" + m.group(1), 16);
            seen.add(va);
        }
        for (Long va : seen) {
            try {
                Function f = getFunctionContaining(toAddr(va));
                if (f == null) {
                    continue;
                }
                out(indent + "-- hop: " + f.getName() + " @ RVA " + rva(f.getEntryPoint().getOffset()) + " --");
                String s = decompileToString(f, maxLines);
                out(s.replace("\n", "\n" + indent.substring(0, Math.max(0, indent.length() - 2))));
            }
            catch (Exception e) {
                out(indent + "[!] hop exception " + e);
            }
        }
    }
}
