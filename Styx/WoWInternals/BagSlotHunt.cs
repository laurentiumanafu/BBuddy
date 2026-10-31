using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Styx.Helpers;
using Styx.Offsets;

namespace Styx.WoWInternals;

/// <summary>
/// The hunt for the bag-slot count: which word in the player's data block goes down by exactly
/// one every time an item is looted.
///
/// Why this shape. On build 70291 nothing readable says "the bags are full":
///
///   * <c>C_Container.GetContainerNumFreeSlots</c> is a Lua function - the Lua route is retired,
///     because calling a registered handler means touching cold or ciphertext pages and it is what
///     crashed the client twice (see CLICKAIM_PORT.md, 2026-10-13);
///   * there is no container or money offset in <see cref="WowBOffsets"/>;
///   * the container objects themselves are in the object manager, and identifying one - item
///     objects have no unit vtable, no name pointer and no level word - is a hunt of its own.
///
/// What CAN be done without a single call into the client is the same trick that found the loot
/// words: read a window of the player's block before and after an event that changes exactly one
/// bag slot, and keep the words whose value moved by exactly that much. A free-slot count goes
/// down by one per item; a used-slot count goes up by one. Anything else - timers, cooldowns,
/// health regeneration, the pointer to the next thing - moves by something else or not at all.
///
/// Every sample here is a plain read of memory the bot already reads. Nothing is written, nothing is
/// called, and the report is a list of candidates with the evidence attached ("went down by exactly
/// 1 on 5 of 5 loots"), not an assertion. When one candidate passes, its offset is used as the
/// bag-full test and the kill/loot counter in the grind loop becomes the fallback it always was.
/// </summary>
internal static class BagSlotHunt
{
    /// <summary>
    /// Turned on from <c>Settings\clickaim-grind.txt</c> (<c>BagSlotHunt = true</c>). It costs one
    /// read of about 1.5 KB per looted body, which is nothing next to the clicks a loot already
    /// sends, so it is on by default until the offset is found.
    /// </summary>
    public static bool Enabled;

    /// <summary>
    /// How many loots before a word is even called a candidate. One sample is a coincidence: a
    /// cooldown that happens to expire at the same moment moves by one too.
    /// </summary>
    private const int MinSamples = 3;

    /// <summary>
    /// The windows of the player's data block that are read, 128 dwords (512 bytes) each - the
    /// largest window <see cref="WowBReader.TryReadWords"/> accepts, and the cap is what makes this
    /// safe rather than a crawl through the whole object.
    ///
    /// They cover the object header (pointers, the handle at 0x38, the name pointer at 0x238) and
    /// the unit block the bot already reads - power at 0x1BC8, health at 0x1E30, level at 0x1E40,
    /// flags at 0x1E60 - plus the space around it, which is where a container array or a slot count
    /// living next to the rest of the player's fields would be.
    /// </summary>
    private static readonly int[] WindowOffsets = { 0x0000, 0x1000, 0x1800, 0x1A00, 0x1C00, 0x1E00, 0x2000, 0x2200, 0x2400 };

    /// <summary>Dwords per window: the reader refuses more than 128 in one read.</summary>
    private const int WindowWords = 128;

    /// <summary>One word's history: how often it moved down or up by exactly one.</summary>
    private sealed class Tally
    {
        public int Samples;          // how many samples this word was part of
        public int DownOne;
        public int UpOne;
        public int Other;
        public uint First;
        public uint Last;
    }

    private static readonly Dictionary<int, Tally> Word = new Dictionary<int, Tally>();
    private static readonly Dictionary<int, uint> Previous = new Dictionary<int, uint>();
    private static bool _havePrevious;
    private static int _samples;
    private static ulong _lastDataPtr;

    /// <summary>Forgets everything. Called at the start of a run.</summary>
    public static void Reset()
    {
        Word.Clear();
        Previous.Clear();
        _havePrevious = false;
        _samples = 0;
        _lastDataPtr = 0;
    }

    /// <summary>
    /// Reads the player's block and compares it with the previous reading. <paramref name="reason"/>
    /// is what moved a bag slot - "looted a body", "sold at the vendor" - and goes into the log so
    /// a sample can be read back against what happened.
    /// </summary>
    public static void Sample(string reason)
    {
        if (!Enabled) return;

        WowBReader? reader = ObjectManager.WowB;
        if (reader == null) return;

        WowBWorldSnapshot? snapshot;
        try
        {
            if (!reader.TryCaptureWorldSnapshot(out snapshot) || snapshot == null) return;
        }
        catch
        {
            return;
        }

        WowBObservedUnit me;
        if (!snapshot.TryGetLocalPlayer(out me)) return;

        WowBReader.WowBUnitFields fields;
        if (!reader.TryGetUnitFields(me.UnitAddress, out fields)) return;
        if (fields.DataPtr <= 0x10000UL) return;

        /* A new data block is a new address: the words in the old one mean nothing against the
         * new one, so the history starts again rather than reporting a wall of deltas. */
        if (_lastDataPtr != 0 && _lastDataPtr != fields.DataPtr) Reset();
        _lastDataPtr = fields.DataPtr;

        int changed = 0;
        for (int w = 0; w < WindowOffsets.Length; w++)
        {
            uint[] words;
            if (!reader.TryReadWords(fields.DataPtr + (ulong)WindowOffsets[w], WindowWords, out words)) continue;

            for (int i = 0; i < words.Length; i++)
            {
                int offset = WindowOffsets[w] + i * 4;
                Tally tally;
                if (!Word.TryGetValue(offset, out tally))
                {
                    tally = new Tally { First = words[i], Last = words[i] };
                    Word[offset] = tally;
                }
                tally.Samples++;
                tally.Last = words[i];

                uint before;
                if (_havePrevious && Previous.TryGetValue(offset, out before) && before != words[i])
                {
                    changed++;
                    long delta = (long)words[i] - (long)before;
                    if (delta == -1) tally.DownOne++;
                    else if (delta == 1) tally.UpOne++;
                    else tally.Other++;
                }

                Previous[offset] = words[i];
            }
        }

        _havePrevious = true;
        _samples++;

        if (_samples <= 5 || _samples % 10 == 0)
        {
            Logging.Write(string.Format(CultureInfo.InvariantCulture,
                "[Bag] sample {0} after {1}: {2} word(s) moved since the last one. The candidate list is "
                + "printed at the end of the run.", _samples, reason, changed));
        }
    }

    /// <summary>
    /// The candidates, best first: words that went down by exactly one on every sample where they
    /// moved at all, then the ones that went up by exactly one - a free-slot count and a used-slot
    /// count look exactly like that, in opposite directions.
    /// </summary>
    public static string Report()
    {
        if (!Enabled) return "";
        if (_samples < MinSamples)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "[Bag] bag slot hunt: {0} sample(s), which is not enough to name an offset (needs {1} looted "
                + "bodies). The hunt keeps running; nothing is guessed in the meantime.",
                _samples, MinSamples);
        }

        var down = new List<KeyValuePair<int, Tally>>();
        var up = new List<KeyValuePair<int, Tally>>();
        foreach (KeyValuePair<int, Tally> pair in Word)
        {
            Tally tally = pair.Value;
            if (tally.DownOne >= MinSamples && tally.Other == 0 && tally.UpOne == 0)
                down.Add(pair);
            else if (tally.UpOne >= MinSamples && tally.Other == 0 && tally.DownOne == 0)
                up.Add(pair);
        }

        down.Sort((a, b) => b.Value.DownOne.CompareTo(a.Value.DownOne));
        up.Sort((a, b) => b.Value.UpOne.CompareTo(a.Value.UpOne));

        var text = new StringBuilder();
        text.AppendFormat(CultureInfo.InvariantCulture,
            "[Bag] bag slot hunt over {0} sample(s): {1} word(s) went DOWN by exactly 1 every time they "
            + "moved, {2} went UP by exactly 1.", _samples, down.Count, up.Count);

        int shown = 0;
        for (int i = 0; i < down.Count && shown < 6; i++, shown++)
        {
            Tally tally = down[i].Value;
            text.AppendLine().AppendFormat(CultureInfo.InvariantCulture,
                "[Bag]   +0x{0:X}  down-by-one {1}/{2}  (now {3}, started {4})  <- a FREE-SLOT count looks like this",
                down[i].Key, tally.DownOne, _samples - 1, tally.Last, tally.First);
        }
        shown = 0;
        for (int i = 0; i < up.Count && shown < 6; i++, shown++)
        {
            Tally tally = up[i].Value;
            text.AppendLine().AppendFormat(CultureInfo.InvariantCulture,
                "[Bag]   +0x{0:X}  up-by-one {1}/{2}  (now {3}, started {4})  <- a USED-SLOT count looks like this",
                up[i].Key, tally.UpOne, _samples - 1, tally.Last, tally.First);
        }

        if (down.Count == 0 && up.Count == 0)
        {
            text.AppendLine().Append("[Bag] No word in these windows moved by exactly one per loot. That means the "
                + "count is NOT in the player's data block on this build - the next place to look is the "
                + "container objects, which needs the pool walk (see hunt/WOWB_DECODED_MODEL.md).");
        }
        else
        {
            text.AppendLine().Append("[Bag] A candidate becomes the bag-full test when it survives ten loots and "
                + "a sale (which moves it the other way by the number of items sold).");
        }

        return text.ToString();
    }
}
