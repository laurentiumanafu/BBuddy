using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GreenMagic;
using Styx.Offsets;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Candidate observations. The 70291 +0x9440 and 70707 +0x9460 contexts
// are not universally validated player inventories or slot counts.
internal static class WowBInventoryProbe
{
    private const ulong Candidate70291 = 0x9440, Lookup70291 = 0x290CFC0, Handler70291 = 0x2F5F2B0;
    private const ulong Candidate70707 = 0x9460, Lookup70707 = 0x2162D00, Handler70707 = 0x26CAA90;
    private const int HeaderSize = 0x28, EntrySize = 16, MaxEntries = 256;
    private const ulong UserLimit = 0x00007FFFFFFFFFFFUL;
    private const string LookupSignature70291 = "48895C2408574883EC208BDA488BF9";
    private const string LookupSignature70707 = "48895C2408574883EC2080792000";
    private const string HandlerSignature = "48895C24084889742418574881EC2004";
    private static readonly object Gate = new object();
    private static Memory? _previousMemory;
    private static WowBReader? _previousReader;
    private static WowBGuid _previousPlayer;
    private static ulong _previousUnit, _previousArray;
    private static byte[]? _previousEntries;

    private static bool Same(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool ValidPlayer(Memory memory, WowBReader reader, WowBObservedUnit player)
    {
        if (!player.IsLocalPlayer || !player.HasPlayerVtable) return false;
        return reader.IsExperimental70707
            ? WowBGrindFactions.TraceBuild70707(memory, reader) && WowBGrindFactions.TraceIdentity70707(memory, reader, player, true)
            : WowBGrindFactions.ValidateDiagnosticUnit(memory, reader, player);
    }

    // 70707 code pages can be inaccessible at runtime. A readable mismatch
    // rejects the probe; an unreadable marker is reported and the separate
    // partial build/identity checks remain mandatory. 70291 stays strict.
    private static bool SignatureMatchesOrUnreadable(Memory memory, ulong rva, string expected, out bool unreadable)
    {
        byte[]? bytes = memory.ReadBytes64(memory.GetAbsolute64(rva), expected.Length / 2);
        unreadable = bytes == null || bytes.Length * 2 != expected.Length;
        return unreadable || Convert.ToHexString(bytes!) == expected;
    }

    // Static 70707 helper has a special-index branch using +0x7D04 and
    // +0x9480 to scan entries starting at 0x23. The helper's context is
    // not proven to be the player; the resulting subtotal is experimental.
    private static string CaptureSpecial70707(Memory memory, WowBReader reader, WowBObservedUnit player,
        ulong candidate, byte[] header, ulong array, byte[] entries, uint count, out (int CandidateSize, int ZeroTags)? observation, bool active = false)
    {
        observation = null;
        const ulong sizeOffset = 0x7D04;
        const int start = 0x23, maxSize = 64;
        if (player.UnitAddress > UserLimit - sizeOffset - 1) return reader.ExperimentalBuildNumber + " special-index research: player address outside bounds.\n";
        bool a = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606EB6UL : reader.IsExperimental70755 ? 0x2473586UL : 0x25691D6UL, "4038A880940000", out bool flagUnreadable);
        bool b = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606EC9UL : reader.IsExperimental70755 ? 0x2473599UL : 0x25691E9UL, "0FB6B8047D0000", out bool sizeUnreadable);
        bool c = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606ED4UL : reader.IsExperimental70755 ? 0x24735A4UL : 0x25691F4UL, "83C723", out bool startUnreadable);
        if (!a || !b || !c) return reader.ExperimentalBuildNumber + " special-index research: readable signature mismatch; unavailable.\n";
        byte[]? size = memory.ReadBytes64(player.UnitAddress + sizeOffset, 1);
        byte[]? sizeAgain = memory.ReadBytes64(player.UnitAddress + sizeOffset, 1);
        if (!Same(size, sizeAgain) || size == null || size.Length != 1 ||
            !Same(header, memory.ReadBytes64(candidate, HeaderSize)) ||
            !Same(entries, entries.Length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(array, entries.Length)) ||
            ClickAimUi.Running != active || ClickAimUi.Paused || ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
            !ValidPlayer(memory, reader, player))
            return reader.ExperimentalBuildNumber + " special-index research: size, array, player or bot state unreadable/changed; unavailable.\n";
        bool aAfter = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606EB6UL : reader.IsExperimental70755 ? 0x2473586UL : 0x25691D6UL, "4038A880940000", out bool flagAfterUnreadable);
        bool bAfter = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606EC9UL : reader.IsExperimental70755 ? 0x2473599UL : 0x25691E9UL, "0FB6B8047D0000", out bool sizeAfterUnreadable);
        bool cAfter = SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? 0x2606ED4UL : reader.IsExperimental70755 ? 0x24735A4UL : 0x25691F4UL, "83C723", out bool startAfterUnreadable);
        if (!aAfter || !bAfter || !cAfter) return reader.ExperimentalBuildNumber + " special-index research: readable signature mismatch after read; unavailable.\n";
        int n = size[0], flag = header[0x20];
        string signatures = "flag=" + (flagUnreadable ? "unreadable" : "matched") + "/" + (flagAfterUnreadable ? "unreadable" : "matched") +
            ", size=" + (sizeUnreadable ? "unreadable" : "matched") + "/" + (sizeAfterUnreadable ? "unreadable" : "matched") +
            ", start=" + (startUnreadable ? "unreadable" : "matched") + "/" + (startAfterUnreadable ? "unreadable" : "matched");
        if (flag == 0 || n > maxSize || (ulong)start + (ulong)n > count)
            return string.Format(CultureInfo.InvariantCulture,
                reader.ExperimentalBuildNumber + " special-index research: raw player+0x7D04={0}, header+0x20={1}; range unavailable or gated; code markers {2}. Not a free-slot verdict.\n", n, flag, signatures);
        int zeroTags = 0;
        for (int i = 0; i < n; i++)
            if ((BitConverter.ToUInt64(entries, (start + i) * EntrySize + 8) >> 58) == 0) zeroTags++;
        observation = (n, zeroTags);
        return string.Format(CultureInfo.InvariantCulture,
            reader.ExperimentalBuildNumber + " special-index research: raw player+0x7D04={0}, header+0x20={1}, raw candidate range=[0x23,0x{2:X}), high-qword high-six-zero={3}; code markers {4}. Context and slot meanings unverified; NOT a free-slot verdict.\n",
            n, flag, start + n, zeroTags, signatures);
    }

    // Caller runs this off the Grind thread. Unknown, incomplete, changing or
    // unsupported observations return false, never a zero-slot verdict.
    public static bool TryCaptureSellingSubtotal(out int zeroTags, out WowBGuid playerGuid)
    {
        zeroTags = 0; playerGuid = default;
        lock (Gate)
        {
            Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
            if (!ClickAimUi.Running || ClickAimUi.Paused || memory == null || reader == null ||
                !memory.Is64BitTarget || !reader.IsExperimental70707) return false;
            try
            {
                using var uncached = memory.TemporaryCacheState(false);
                if (!SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? Wow70790Offsets.InventoryLookup : reader.IsExperimental70755 ? Wow70755Offsets.InventoryLookup : Lookup70707, LookupSignature70707, out _) ||
                    !SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? Wow70790Offsets.InventoryHandler : reader.IsExperimental70755 ? Wow70755Offsets.InventoryHandler : Handler70707, HandlerSignature, out _) ||
                    !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                    !before.TryGetLocalPlayer(out WowBObservedUnit player) || !ValidPlayer(memory, reader, player) ||
                    player.UnitAddress > UserLimit - Candidate70707 - HeaderSize) return false;
                ulong candidate = player.UnitAddress + Candidate70707;
                byte[]? header = memory.ReadBytes64(candidate, HeaderSize);
                if (header == null || header.Length != HeaderSize) return false;
                uint count = BitConverter.ToUInt32(header, 0);
                ulong array = BitConverter.ToUInt64(header, 8);
                if (count == 0 || count > MaxEntries || array < 0x10000 || array > UserLimit - (ulong)count * EntrySize) return false;
                int length = checked((int)count * EntrySize);
                byte[]? entries = memory.ReadBytes64(array, length);
                if (!Same(entries, memory.ReadBytes64(array, length)) || !Same(header, memory.ReadBytes64(candidate, HeaderSize))) return false;
                CaptureSpecial70707(memory, reader, player, candidate, header, array, entries!, count, out var special, true);
                WowBContainerResearchProbe.Capture(memory, reader, player, out var containers, true);
                CaptureSpecial70707(memory, reader, player, candidate, header, array, entries!, count, out var specialAfter, true);
                if (!special.HasValue || special.Value.CandidateSize == 0 || !containers.HasValue ||
                    !specialAfter.HasValue || special.Value != specialAfter.Value ||
                    !SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? Wow70790Offsets.InventoryLookup : reader.IsExperimental70755 ? Wow70755Offsets.InventoryLookup : Lookup70707, LookupSignature70707, out _) ||
                    !SignatureMatchesOrUnreadable(memory, reader.IsExperimental70790 ? Wow70790Offsets.InventoryHandler : reader.IsExperimental70755 ? Wow70755Offsets.InventoryHandler : Handler70707, HandlerSignature, out _) ||
                    !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                    !after.TryGetLocalPlayer(out WowBObservedUnit final) || after.LocalGuid != before.LocalGuid ||
                    final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress ||
                    !ValidPlayer(memory, reader, final) || !ClickAimUi.Running || ClickAimUi.Paused ||
                    ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
                // 70755 excludes only explicitly classified specialty instances;
                // an unknown bag can neither authorize nor suppress a sale by guesswork.
                if (reader.UsesOrdinaryBagPolicy && containers.Value.Unclassified != 0) return false;
                zeroTags = special.Value.ZeroTags + (reader.UsesOrdinaryBagPolicy ? containers.Value.OrdinaryZeroTags : containers.Value.ZeroTags);
                playerGuid = player.Guid;
                return true;
            }
            catch { return false; }
        }
    }

    internal static List<WowBContainerResearchProbe.BagChoice> ObservedBags()
    {
        lock (Gate)
        {
            Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
            return memory == null || reader == null || !reader.UsesOrdinaryBagPolicy
                ? new List<WowBContainerResearchProbe.BagChoice>()
                : WowBContainerResearchProbe.LastChoices(memory, reader);
        }
    }

    internal static bool ClassifyBag(WowBContainerResearchProbe.BagChoice selected, WowBBagKind kind, out string message)
    {
        message = "Stop the bot and take a fresh bag snapshot first.";
        lock (Gate)
        {
            if (ClickAimUi.Running || ClickAimUi.Paused || ObjectManager.WowB?.UsesOrdinaryBagPolicy != true) return false;
            Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
            Capture(); // revalidate the attachment, identities, signatures and candidate set
            if (memory == null || reader == null || ObjectManager.Wow != memory || ObjectManager.WowB != reader) return false;
            bool found = false;
            foreach (var bag in WowBContainerResearchProbe.LastChoices(memory, reader))
                if (bag.Player == selected.Player && bag.Bag == selected.Bag && bag.Size == selected.Size) found = true;
            if (!found || ClickAimUi.Running || ClickAimUi.Paused)
            { message = "Bag, player or bot state changed; take another snapshot."; return false; }
            if (!WowBBagClassification.Save(selected.Player, selected.Bag, kind, out message)) return false;
            message = "Saved " + kind + " for this bag instance. Take another snapshot to check the ordinary-bag subtotal.";
            return true;
        }
    }

    public static string Capture()
    {
        lock (Gate)
        {
            WowBContainerResearchProbe.InvalidateChoices();
            if (ClickAimUi.Running || ClickAimUi.Paused) return "Stop the bot and routes before the inventory probe.";
            Memory? memory = ObjectManager.Wow; WowBReader? reader = ObjectManager.WowB;
            if (memory == null || reader == null || !memory.Is64BitTarget) return "Attach to a registered 70291, 70707, 70755 or 70790 client and enter the world first.";
            try
            {
                using var uncached = memory.TemporaryCacheState(false);
                bool experimental = reader.IsExperimental70707;
                ulong candidateOffset = experimental ? Candidate70707 : Candidate70291;
                ulong lookupRva = reader.IsExperimental70790 ? Wow70790Offsets.InventoryLookup : reader.IsExperimental70755 ? Wow70755Offsets.InventoryLookup : experimental ? Lookup70707 : Lookup70291;
                ulong handlerRva = reader.IsExperimental70790 ? Wow70790Offsets.InventoryHandler : reader.IsExperimental70755 ? Wow70755Offsets.InventoryHandler : experimental ? Handler70707 : Handler70291;
                string lookupSignature = experimental ? LookupSignature70707 : LookupSignature70291;
                if (!SignatureMatchesOrUnreadable(memory, lookupRva, lookupSignature, out bool lookupUnreadable) ||
                    !SignatureMatchesOrUnreadable(memory, handlerRva, HandlerSignature, out bool handlerUnreadable) ||
                    (!experimental && (lookupUnreadable || handlerUnreadable)))
                    return "Inventory trace signatures unreadable or different; no probe captured.";
                if (!reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? before) || before == null ||
                    !before.TryGetLocalPlayer(out WowBObservedUnit player) || !ValidPlayer(memory, reader, player) ||
                    player.UnitAddress > UserLimit - candidateOffset - HeaderSize)
                    return "Player or build identity unavailable; no probe captured.";
                ulong candidate = player.UnitAddress + candidateOffset;
                byte[]? header = memory.ReadBytes64(candidate, HeaderSize);
                if (header == null || header.Length != HeaderSize) return "Candidate header unreadable; no probe captured.";
                uint count = BitConverter.ToUInt32(header, 0);
                ulong array = BitConverter.ToUInt64(header, 8);
                if (count > MaxEntries || (count != 0 && (array < 0x10000 || array > UserLimit - (ulong)count * EntrySize)))
                    return "Candidate count or array address outside diagnostic bounds; no probe captured.";
                int length = checked((int)count * EntrySize);
                byte[]? first = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(array, length);
                byte[]? second = length == 0 ? Array.Empty<byte>() : memory.ReadBytes64(array, length);
                byte[]? headerAfter = memory.ReadBytes64(candidate, HeaderSize);
                if (!Same(first, second) || !Same(header, headerAfter) || ClickAimUi.Running || ClickAimUi.Paused ||
                    ObjectManager.Wow != memory || ObjectManager.WowB != reader ||
                    !reader.TryCaptureWorldSnapshot(out WowBWorldSnapshot? after) || after == null ||
                    !after.TryGetLocalPlayer(out WowBObservedUnit final) || after.LocalGuid != before.LocalGuid ||
                    final.UnitAddress != player.UnitAddress || final.RecordAddress != player.RecordAddress ||
                    !ValidPlayer(memory, reader, final) || ClickAimUi.Running || ClickAimUi.Paused)
                    return "Candidate, player, attachment or bot state changed/unreadable; probe discarded.";

                bool comparable = _previousMemory == memory && _previousReader == reader && _previousPlayer == player.Guid &&
                    _previousUnit == player.UnitAddress && _previousArray == array && _previousEntries != null && _previousEntries.Length == length;
                var text = new StringBuilder();
                text.AppendLine((experimental ? reader.ExperimentalBuildNumber.ToString(CultureInfo.InvariantCulture) : "70291") + " read-only inventory research snapshot; candidate only, not a bag or full-slot verdict.");
                if (experimental)
                    text.AppendLine(reader.ExperimentalBuildNumber.ToString(CultureInfo.InvariantCulture) + " static context is not proven to be the player object; code signatures: lookup=" +
                        (lookupUnreadable ? "unreadable" : "matched") + ", handler=" + (handlerUnreadable ? "unreadable" : "matched") + ".");
                text.AppendFormat(CultureInfo.InvariantCulture, "UTC {0:O}; player GUID {1}; player object 0x{2:X}; candidate header +0x{3:X}=0x{4:X}; raw header={5}\n",
                    DateTime.UtcNow, player.Guid, player.UnitAddress, candidateOffset, candidate, Convert.ToHexString(header));
                text.AppendFormat(CultureInfo.InvariantCulture, "candidate count={0}; array=0x{1:X}; stride=16; previous={2}\n", count, array, comparable ? "comparable" : "none or different context");
                int changed = 0;
                for (int i = 0; i < (int)count; i++)
                {
                    int offset = i * EntrySize;
                    string current = Convert.ToHexString(first!, offset, EntrySize);
                    if (comparable)
                    {
                        string prior = Convert.ToHexString(_previousEntries!, offset, EntrySize);
                        if (prior != current) { changed++; text.AppendFormat(CultureInfo.InvariantCulture, "entry[{0}] {1} -> {2}\n", i, prior, current); }
                    }
                    else text.AppendFormat(CultureInfo.InvariantCulture, "entry[{0}] {1}\n", i, current);
                }
                if (comparable) text.AppendFormat(CultureInfo.InvariantCulture, "changed entries={0}; identical bytes do not establish a slot was empty.\n", changed);
                if (experimental)
                {
                    text.Append(CaptureSpecial70707(memory, reader, player, candidate, header, array, first!, count, out var special));
                    text.Append(WowBContainerResearchProbe.Capture(memory, reader, player, out var containers));
                    // Recheck the player-range bytes after the container scan;
                    // cross-process sampling is still not atomic.
                    if (special.HasValue && containers.HasValue)
                    {
                        CaptureSpecial70707(memory, reader, player, candidate, header, array, first!, count, out var specialAfter);
                        if (specialAfter.HasValue && special.Value == specialAfter.Value)
                        {
                            text.AppendFormat(CultureInfo.InvariantCulture,
                                "{4} research subtotal: candidate sizes={0}, zero-tags={1}; player-range plus {2} resolved container candidates, {3} zero identities not classified. May overlap or omit bags; NOT a verified total or free-slot verdict.\n",
                                special.Value.CandidateSize + containers.Value.CandidateSize, special.Value.ZeroTags + containers.Value.ZeroTags,
                                containers.Value.Resolved, containers.Value.ZeroIdentities, reader.ExperimentalBuildNumber);
                            if (reader.UsesOrdinaryBagPolicy)
                            {
                                if (containers.Value.Unclassified == 0)
                                    text.AppendFormat(CultureInfo.InvariantCulture, reader.ExperimentalBuildNumber + " ordinary-bag candidate subtotal: sizes={0}, zero-tags={1}; specialty bags excluded by user classification; still experimental.\n",
                                        special.Value.CandidateSize + containers.Value.OrdinarySize, special.Value.ZeroTags + containers.Value.OrdinaryZeroTags);
                                else text.AppendLine(reader.ExperimentalBuildNumber + " ordinary-bag candidate subtotal unavailable: classify every observed bag as ordinary or specialty first.");
                            }
                        }
                        else { WowBContainerResearchProbe.InvalidateChoices(); text.AppendLine(reader.ExperimentalBuildNumber + " research subtotal unavailable: player-range candidate changed or became unreadable."); }
                    }
                    else { WowBContainerResearchProbe.InvalidateChoices(); text.AppendLine(reader.ExperimentalBuildNumber + " research subtotal unavailable: one or more required candidates unresolved or unreadable."); }
                }
                text.AppendLine("Uninterpreted data and process addresses are sensitive. Reads are non-atomic; no inventory, item, bag capacity or mail eligibility is verified.");
                _previousMemory = memory; _previousReader = reader; _previousPlayer = player.Guid;
                _previousUnit = player.UnitAddress; _previousArray = array; _previousEntries = first;
                return text.ToString();
            }
            catch (Exception ex) { return "Inventory probe failed: " + ex.Message; }
        }
    }
}
