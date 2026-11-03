using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using GreenMagic;
using Styx.Helpers;

namespace Styx.WoWInternals
{
    /// <summary>
    /// Finds functions and globals in the attached client by BYTE PATTERN, so a new
    /// build does not need its offsets re-derived from scratch.
    ///
    /// This is a port of two Winkr tools onto this codebase:
    ///
    ///   * <c>Winkr/dll/core/dinkr_scanner.c</c> - scan every readable committed page
    ///     of the module image; if exactly one place matches, that is the address;
    ///     otherwise fall back to the hard-coded RVA.
    ///   * <c>tools/resig.py</c> - when the full pattern no longer matches, shorten it
    ///     from the TAIL one byte at a time and report where the count drops from
    ///     "thousands" to a handful. That is the point where the function's prologue
    ///     ends and the instruction that changed begins, and the handful are candidates.
    ///
    /// The table below is copied from <c>Winkr/shared/sig_defs.h</c>
    /// (github.com/laurentiumanf/Winkr-Public, build 69547 / retail 68453) together
    /// with the RVAs from <c>rva_defs.h</c>. Those RVAs are for OTHER builds - they are
    /// printed as "where Winkr saw it", never used as an address here. On build 70291 a
    /// pattern either matches or it does not, and the report says which.
    ///
    /// Why this exists: the movement work kept being blocked on "find the offset for
    /// X". A pattern scan answers that question by running, once, in the client, in a
    /// second - instead of by opening Ghidra.
    /// </summary>
    internal static class WowBSigScan
    {
        /// <summary>One entry of the ported table.</summary>
        private sealed class Sig
        {
            public string Name;        // Winkr's label
            public uint WinkrRva;      // where that build had it - NOT used as an address
            public string RvaBuild;    // which build the RVA belongs to
            public string Note;
            public byte[] Pattern;
            public string Mask;        // 'x' = must equal, '?' = wildcard
            public bool SkipScan;      // Winkr marks these "use the RVA, do not scan"
        }

        private static Sig S(string name, uint rva, string rvaBuild, string note,
                             string pattern, string mask, bool skipScan = false)
        {
            var bytes = new List<byte>();
            for (int i = 0; i + 3 < pattern.Length + 1; i += 4)
            {
                if (pattern[i] != '\\') break;
                bytes.Add(byte.Parse(pattern.Substring(i + 2, 2), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture));
            }
            return new Sig
            {
                Name = name, WinkrRva = rva, RvaBuild = rvaBuild, Note = note,
                Pattern = bytes.ToArray(), Mask = mask, SkipScan = skipScan
            };
        }

        /// <summary>
        /// The subset of Winkr's table that this project actually asks questions about.
        /// Patterns are byte-for-byte from <c>sig_defs.h</c>; each carries the build whose
        /// RVA it sits beside there.
        /// </summary>
        private static readonly Sig[] Table =
        {
            S("HANDLE_TERRAIN_CLICK", 0x1C06240, "68887",
              "Winkr's own comment: this is the Spell_C GROUND-TARGET handler (targetingFlags & 0x60) - a1=GUID128*, a2=Vec3*, a3=flag, a4=cast context. It is NOT click-to-move.",
              "\x48\x89\x5C\x24\x10\x48\x89\x6C\x24\x18\x48\x89\x74\x24\x20\x57\x48\x83\xEC\x40\x49\x8B\xF1\x48\x8B\xEA\x8B\xF9\xE8\x00\x00\x00\x00\x48\x8B\xD8\x48\x85\xC0",
              "xxxxxxxxxxxxxxxxxxxxxxxxxxxxx????xxxxxx", false),
            S("INTERACT_UNIT", 0x1F10F60, "68887",
              "right-click a unit - the closest thing to a click handler in Winkr's table, and still not a move",
              "\x40\x55\x56\x48\x83\xEC\x28\x8B\xF2\x48\x8B\xE9\xE8\x00\x00\x00\x00\x48\x85\xC0\x75\x09\xB0\x01\x48\x83\xC4\x28\x5E\x5D\xC3\x48",
              "xxxxxxxxxxxxx????xxxxxxxxxxxxxxx", false),
            S("IS_VALUE_NOT_SECRET", 0x2D17E90, "69547",
              "taint: the secret-value gate protected calls walk through",
              "\x48\x83\xEC\x28\xE8\x00\x00\x00\x00\x0F\xB6\x40\x09\x48\x83\xC4\x28\xC3",
              "xxxxx????xxxxxxxxx", false),
            S("LUAG_SECRETERROR", 0x2D152C0, "69547",
              "taint: the Lua error raiser the gates call",
              "\x48\x8B\xC4\x48\x89\x58\x10\x48\x89\x68\x18\x48\x89\x70\x20\x57\x48\x81\xEC\x80\x00\x00\x00\xF6\x05\x00\x00\x00\x00\x04\x49\x8B\xF0\x48\x8B\xDA\x48\x8B\xF9\x74\x31\xC7\x40\xB8\x04\x00\x00\x00\x48\x8D\x54\x24\x40\x4C\x89\x40\xE0",
              "xxxxxxxxxxxxxxxxxxxxxxxxx????xxxxxxxxxxxxxxxxxxxxxxxxxxxx", false),
            S("LUA_SETSECRET", 0x530890, "69547",
              "taint: marks a Lua value secret",
              "\x48\x83\xEC\x28\xE8\x00\x00\x00\x00\xC6\x40\x09\x01\x48\x83\xC4\x28\xC3",
              "xxxxx????xxxxxxxxx", false),
            S("LUA_NEWSTATE", 0x2D24520, "69547",
              "the Lua state constructor",
              "\x48\x89\x5C\x24\x18\x55\x56\x41\x56\x48\x83\xEC\x20\x48\x8B\xF2\x45\x8B\xF0\x48\x8B\xE9\x41\xB9\xB8\x02\x00\x00\x48\x8B\xCE\x45\x33\xC0\x33\xD2\xFF\xD5",
              "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", false),
            S("LUA_PCALL", 0x2D18290, "69547",
              "lua_pcall - needed if a call is ever made through the Lua state",
              "\x48\x89\x5C\x24\x08\x48\x89\x74\x24\x10\x57\x48\x83\xEC\x40\x41\x8B\xF0\x44\x8B\xD2\x48\x8B\xD9\x45\x85\xC9\x75\x04\x33\xC0\xEB\x0C\x41\x8B\xD1\xE8\x00\x00\x00\x00\x48\x2B\x43\x58",
              "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx????xxxx", false),
            S("C_API_SECRET_ARG_GATE", 0x2D16E50, "69547",
              "the second half of Winkr's 3-way g_taint cross-check",
              "\x48\x83\xEC\x28\x4C\x8B\xD1\xE8\x00\x00\x00\x00\x80\x78\x09\x00\x74\x27\x49\x8B\x42\x40\x0F\xBE\x48\x24\x85\xC9\x74\x14\x83\xF9",
              "xxxxxxxx????xxxxxxxxxxxxxxxxxxxx", false),
            S("g_pObjectMgr", 0x5D3E2C0, "69547",
              "mov rax,[rip+disp32]: a raw hit is an instruction, not an address",
              "\x48\x8B\x05\x00\x00\x00\x00\x48\x8B\xF1\x33\xFF\x33\xDB",
              "xxx????xxxxxxx", true),
        };

        /// <summary>Chunk size for the scan. Big enough to be cheap, small enough to keep
        /// the per-chunk allocation short-lived.</summary>
        private const int ChunkBytes = 1 << 20;

        /// <summary>A full pattern with more matches than this is not "one function".</summary>
        private const int MaxMatches = 16;

        /// <summary>Below this prefix length a pattern is too short to mean anything.</summary>
        private const int MinPrefix = 8;

        public static void Run()
        {
            try
            {
                Memory? mem = StyxWoW.Memory;
                if (mem == null)
                {
                    Logging.Write("[Sig] no memory reader attached, so there is nothing to scan");
                    return;
                }

                ulong imageBase = mem.GetAbsolute64(0);
                uint imageSize = ReadSizeOfImage(mem, imageBase);
                if (imageSize == 0)
                {
                    Logging.Write("[Sig] could not read the PE header at 0x"
                                  + imageBase.ToString("X", CultureInfo.InvariantCulture)
                                  + " - no SizeOfImage, so no scan");
                    return;
                }

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] scanning 0x{0:X} .. 0x{1:X} ({2:F1} MB) for {3} pattern(s) from Winkr's "
                    + "sig_defs.h (builds 68887/69547 - this client is not one of them, which is "
                    + "the point)",
                    imageBase, imageBase + imageSize, imageSize / 1048576.0, Table.Length));

                int resolved = 0;
                foreach (Sig sig in Table)
                {
                    if (Scan(mem, imageBase, imageSize, sig)) resolved++;
                    Thread.Sleep(0);
                }

                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] done: {0} of {1} pattern(s) resolve on this build. An entry below that "
                    + "says \"no prefix matched\" was rewritten, not moved - no amount of scanning "
                    + "will find it, and calling its Winkr RVA would be jumping into the middle of "
                    + "some unrelated function.", resolved, Table.Length));
            }
            catch (Exception ex)
            {
                Logging.Write("[Sig] the scan failed: " + ex.Message);
            }
        }

        private static uint ReadSizeOfImage(Memory mem, ulong imageBase)
        {
            byte[]? head = mem.ReadBytes64(imageBase, 0x400);
            if (head == null || head.Length < 0x100) return 0;
            int lfanew = BitConverter.ToInt32(head, 0x3C);
            if (lfanew <= 0 || lfanew + 0x60 > head.Length) return 0;
            if (head[lfanew] != 'P' || head[lfanew + 1] != 'E') return 0;
            return BitConverter.ToUInt32(head, lfanew + 0x50);
        }

        /// <summary>
        /// Scans the image for one pattern and reports what it found. Returns true when
        /// exactly one place matched, which is the case where the pattern gives an address.
        /// </summary>
        private static bool Scan(Memory mem, ulong imageBase, uint imageSize, Sig sig)
        {
            int n = Math.Min(sig.Pattern.Length, sig.Mask.Length);
            if (n < MinPrefix)
            {
                Logging.Write("[Sig] " + sig.Name + ": pattern is too short to scan");
                return false;
            }

            var pat = new byte[n];
            Array.Copy(sig.Pattern, pat, n);
            string mask = sig.Mask.Substring(0, n);

            List<ulong> hits = FindAll(mem, imageBase, imageSize, pat, mask, MaxMatches);

            if (sig.SkipScan)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] {0}: {1} raw match(es). Winkr's RVA {2:X} (build {3}) is an instruction "
                    + "operand, not an address - {4}",
                    sig.Name, hits.Count, sig.WinkrRva, sig.RvaBuild, sig.Note));
                if (hits.Count > 0 && HitsLookLikeRipRelative(mem, hits, pat))
                    Logging.Write("[Sig]   ... but the matches are rip-relative movs; the global is "
                                  + "match + 7 + disp32. Reading the disp gives the address to try.");
                return false;
            }

            if (hits.Count == 1)
            {
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] {0}: ONE match at RVA 0x{1:X} (absolute 0x{2:X}). Winkr had 0x{3:X} "
                    + "on {4}. {5}",
                    sig.Name, hits[0] - imageBase, hits[0], sig.WinkrRva, sig.RvaBuild, sig.Note));
                return true;
            }

            if (hits.Count > 1)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < hits.Count && i < 6; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("0x").Append((hits[i] - imageBase).ToString("X", CultureInfo.InvariantCulture));
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] {0}: {1}+ matches ({2}) - ambiguous, so this pattern cannot name a "
                    + "function here. {3}", sig.Name, hits.Count, sb, sig.Note));
                return false;
            }

            /* No match on the full pattern: shorten from the tail, Winkr's resig.py idea.
             * The prefix that first matches is the prologue; what changed is whatever
             * came after it. */
            for (int cut = n - 1; cut >= MinPrefix; cut--)
            {
                var shortPat = new byte[cut];
                Array.Copy(pat, shortPat, cut);
                List<ulong> partial = FindAll(mem, imageBase, imageSize, shortPat, mask.Substring(0, cut), MaxMatches);
                if (partial.Count == 0) continue;

                var sb = new StringBuilder();
                for (int i = 0; i < partial.Count && i < 8; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("RVA 0x").Append((partial[i] - imageBase).ToString("X", CultureInfo.InvariantCulture));
                }
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Sig] {0}: full pattern does not match. First {1} of {2} bytes match at "
                    + "{3} place(s): {4}. Compare those prologues against Winkr's {5} before "
                    + "believing any of them. {6}",
                    sig.Name, cut, n, partial.Count, sb, sig.RvaBuild, sig.Note));
                return false;
            }

            Logging.Write("[Sig] " + sig.Name + ": no prefix of the pattern matches anywhere - the "
                          + "function was rewritten, not moved");
            return false;
        }

        private static bool HitsLookLikeRipRelative(Memory mem, List<ulong> hits, byte[] pat)
        {
            return pat.Length > 3 && pat[0] == 0x48 && pat[1] == 0x8B && pat[2] == 0x05;
        }

        /// <summary>
        /// Reads the image in chunks and collects pattern matches. The overlap is the
        /// pattern length so a match straddling a chunk boundary is not lost.
        /// </summary>
        private static List<ulong> FindAll(Memory mem, ulong imageBase, uint imageSize,
                                           byte[] pat, string mask, int limit)
        {
            var found = new List<ulong>();
            int n = pat.Length;
            ulong end = imageBase + imageSize;

            for (ulong at = imageBase; at < end; at += ChunkBytes)
            {
                int len = (int)Math.Min((ulong)ChunkBytes + (ulong)n, end - at);
                byte[]? buf = mem.ReadBytes64(at, len);
                if (buf == null || buf.Length < n) continue;

                int last = buf.Length - n;
                for (int i = 0; i <= last; i++)
                {
                    if (buf[i] != pat[0] && mask[0] == 'x') continue;
                    bool ok = true;
                    for (int j = 1; j < n; j++)
                    {
                        if (mask[j] == 'x' && buf[i + j] != pat[j]) { ok = false; break; }
                    }
                    if (!ok) continue;
                    ulong abs = at + (ulong)i;
                    // A match in the overlap region was already reported by the last chunk.
                    if (found.Count == 0 || (ulong)found[found.Count - 1] != abs) found.Add(abs);
                    if (found.Count >= limit) return found;
                }
            }

            return found;
        }
    }
}
