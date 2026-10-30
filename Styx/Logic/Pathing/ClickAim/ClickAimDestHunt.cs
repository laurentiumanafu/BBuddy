using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using GreenMagic;
using Styx.Helpers;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

/// <summary>
/// Finds where the client keeps the point it is walking to, by watching one click.
///
/// Why this and not another aim model. Every attempt to aim a click has needed the
/// camera's ORIENTATION - yaw and pitch - and the 2026-10-29 runs say plainly that the
/// camera position this project can read is not enough to get it:
///
///   * drag 150 px turned the camera -10.4, then -4.6, then +4.6, then -6.3 deg, and
///     with the character standing still for nine consecutive ticks the heading
///     climbed 109.1, 113.8, 118.4, 123.1, ... at exactly +4.6 deg per tick. That is
///     not an orientation; it is something that drifts.
///   * the same centre click (0, -140 px) walked 5.8, 6.5, 7.2, 23.8, 12.9, 14.0,
///     23.8, 7.8 and 63.1 yd on different ticks of the same run. A pixel offset cannot
///     mean a distance if those are all the same pixel - the ray's intersection with
///     the ground depends on the camera PITCH, and the pitch is not in the model.
///
/// But every one of those clicks did move the character, so the client must hold the
/// destination somewhere while it walks. If it does, the bot can write the point the
/// mesh wants into that field and never aim a click at a pixel again: no camera, no
/// pitch, no scale, no drag.
///
/// How it is found, with no reversing at all:
///
///   1. wait for the character to stand still, note where it is;
///   2. click, and snapshot a window of the player record while the character is
///      WALKING (that snapshot holds the destination);
///   3. wait for it to stop; where it stopped IS the destination it walked to;
///   4. in the snapshot, look for a float triple that equals that destination and does
///      NOT equal where the character was when the snapshot was taken. The position
///      field fails that test by construction - it holds the moving position - and the
///      destination passes it;
///   5. repeat. An offset that turns up in every trial is the field.
/// </summary>
internal static class ClickAimDestHunt
{
    /// <summary>How far from the record pointer the scan reaches. The transform and
    /// movement state live near the top of the record; 2 KB either side covers them.</summary>
    private const int ScanBack = 0x80;
    private const int ScanBytes = 0x800;

    /// <summary>A trial with a shorter walk than this teaches nothing about a destination.</summary>
    private const double MinTrialWalkYd = 4.0;

    private const int Trials = 3;
    private const int PollMs = 100;
    private const int RestMs = 600;
    private const int MaxWaitMs = 30000;

    public static void Run()
    {
        try
        {
            Memory? mem = StyxWoW.Memory;
            WowBReader? reader = ObjectManager.WowB;
            if (mem == null || reader == null)
            {
                Logging.Write("[Dest] no reader attached; nothing to hunt");
                return;
            }

            IntPtr hwnd = mem.WindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                Logging.Write("[Dest] no game window handle; cannot click");
                return;
            }

            var seenIn = new Dictionary<int, int>();     // offset -> trials it appeared in
            var example = new Dictionary<int, string>();

            for (int trial = 1; trial <= Trials; trial++)
            {
                WowBReader.WowBPoolObject obj;
                if (!reader.TryGetPlayerObject(out obj) || obj.IsEmpty)
                {
                    Logging.Write("[Dest] no player record; stopping");
                    return;
                }

                ulong record = obj.RecordPtr;
                double sx, sy;
                if (!ClickAimPositions.TryRead(out sx, out sy))
                {
                    Logging.Write("[Dest] cannot read the player position; stopping");
                    return;
                }

                if (!WaitForRest(out sx, out sy, RestMs))
                    Logging.Write("[Dest] the character is still moving; trying anyway");

                double startX = sx, startY = sy;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Dest] trial {0}/{1}: at rest at ({2:F1}, {3:F1}); record 0x{4:X}; clicking the middle "
                    + "of the screen to start a walk", trial, Trials, startX, startY, record));

                ClickAimInput.Click(hwnd, 0, -140);

                // Snapshot while it walks, and remember where it was at that instant.
                byte[]? mid = null;
                double midX = 0, midY = 0;
                int waited = 0;
                while (waited < MaxWaitMs)
                {
                    Thread.Sleep(PollMs);
                    waited += PollMs;
                    double nx, ny;
                    if (!ClickAimPositions.TryRead(out nx, out ny))
                        continue;

                    if (mid == null && ClickAimMath.Len(nx - startX, ny - startY) >= 2.0)
                    {
                        mid = mem.ReadBytes64(record - ScanBack, ScanBytes);
                        midX = nx;
                        midY = ny;
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Dest] snapshot taken after walking {0:F1} yd, at ({1:F1}, {2:F1})",
                            ClickAimMath.Len(nx - startX, ny - startY), nx, ny));
                    }

                    if (mid != null && ClickAimMath.Len(nx - midX, ny - midY) >= 1.0
                        && AtRest(nx, ny))
                        break;
                }

                double fx, fy;
                if (!ClickAimPositions.TryRead(out fx, out fy))
                {
                    Logging.Write("[Dest] lost the position after the click; trial skipped");
                    continue;
                }

                double walked = ClickAimMath.Len(fx - startX, fy - startY);
                if (mid == null || walked < MinTrialWalkYd)
                {
                    Logging.Write(string.Format(CultureInfo.InvariantCulture,
                        "[Dest] trial {0}: the click walked {1:F1} yd - too little to learn from. "
                        + "Move the character to open ground and run this again.", trial, walked));
                    continue;
                }

                List<int> hits = FindCandidates(mid, midX, midY, fx, fy, 1.0);
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Dest] trial {0}: walked {1:F1} yd and stopped at ({2:F1}, {3:F1}); {4} float "
                    + "triple(s) hold that point but not the position at snapshot time",
                    trial, walked, fx, fy, hits.Count));

                if (hits.Count == 0)
                {
                    Logging.Write("[Dest]   none - the destination is not a plain Vec3 in this window, or "
                                  + "it is cleared as soon as the walk ends. Widen ScanBytes and try again.");
                    continue;
                }

                foreach (int off in hits)
                {
                    int key = off - ScanBack;      // report relative to the record pointer
                    int count;
                    seenIn.TryGetValue(key, out count);
                    seenIn[key] = count + 1;
                    example[key] = string.Format(CultureInfo.InvariantCulture,
                        "({0:F2}, {1:F2})", fx, fy);
                    if (hits.IndexOf(off) < 8)
                        Logging.Write(string.Format(CultureInfo.InvariantCulture,
                            "[Dest]   record {0:+#;-#;0}0x{1:X} (absolute 0x{2:X})", key < 0 ? "-" : "+",
                            Math.Abs(key), record + (ulong)(long)key));
                }

                Thread.Sleep(300);
            }

            Logging.Write("[Dest] --- summary ---");
            bool any = false;
            foreach (KeyValuePair<int, int> kv in seenIn)
            {
                if (kv.Value < 2) continue;
                any = true;
                Logging.Write(string.Format(CultureInfo.InvariantCulture,
                    "[Dest] record + 0x{0:X} appeared in {1}/{2} trials (destination was {3} yd "
                    + "from the player). This is the field to try writing.",
                    kv.Key, kv.Value, Trials, example[kv.Key]));
            }

            if (!any)
            {
                Logging.Write("[Dest] no offset appeared in more than one trial. That usually means the "
                              + "destination is not stored as an (x, y, z) triple near the record, so the "
                              + "next step is the wider scan described above - not another click model.");
            }
            else
            {
                Logging.Write("[Dest] next step, and it is a small one: write the waypoint into that offset "
                              + "while the character is walking from a real click, and see whether the "
                              + "client follows. If it does, the mover needs no camera and no pixels at all.");
            }
        }
        catch (Exception ex)
        {
            Logging.Write("[Dest] the hunt failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Every 4-byte-aligned float triple in <paramref name="buf"/> that is within
    /// <paramref name="tolYd"/> of (fx, fy) but not of (cx, cy): a destination the
    /// character was walking to, as opposed to the position it was passing through.
    ///
    /// Pure and static so it can be checked without a game: see <see cref="SelfTest"/>.
    /// </summary>
    public static List<int> FindCandidates(byte[] buf, double cx, double cy, double fx, double fy,
                                           double tolYd)
    {
        var hits = new List<int>();
        if (buf == null) return hits;

        for (int i = 0; i + 12 <= buf.Length; i += 4)
        {
            float x = BitConverter.ToSingle(buf, i);
            float y = BitConverter.ToSingle(buf, i + 4);
            float z = BitConverter.ToSingle(buf, i + 8);

            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) continue;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) continue;
            if (Math.Abs(x) > 100000f || Math.Abs(y) > 100000f || Math.Abs(z) > 100000f) continue;

            // At the destination...
            if (Math.Abs(x - fx) > tolYd || Math.Abs(y - fy) > tolYd) continue;
            // ...and not at the place the character was when the snapshot was taken.
            if (Math.Abs(x - cx) <= tolYd && Math.Abs(y - cy) <= tolYd) continue;

            hits.Add(i);
        }

        return hits;
    }

    private static bool AtRest(double x, double y)
    {
        // Two reads 250 ms apart that agree to 5 cm: the character has stopped.
        Thread.Sleep(250);
        double nx, ny;
        if (!ClickAimPositions.TryRead(out nx, out ny)) return false;
        return ClickAimMath.Len(nx - x, ny - y) < 0.05;
    }

    private static bool WaitForRest(out double x, out double y, int restMs)
    {
        x = 0;
        y = 0;
        if (!ClickAimPositions.TryRead(out x, out y)) return false;

        double lx = x, ly = y;
        int still = 0;
        for (int waited = 0; waited < MaxWaitMs; waited += PollMs)
        {
            Thread.Sleep(PollMs);
            double nx, ny;
            if (!ClickAimPositions.TryRead(out nx, out ny)) continue;
            if (ClickAimMath.Len(nx - lx, ny - ly) < 0.05) still += PollMs;
            else still = 0;
            lx = nx;
            ly = ny;
            if (still >= restMs)
            {
                x = nx;
                y = ny;
                return true;
            }
        }

        x = lx;
        y = ly;
        return false;
    }

    /// <summary>
    /// Checks the finder against synthetic records: the position field must never be
    /// reported (it holds where the character was, not where it is going), the
    /// destination must be, and noise must not be.
    /// </summary>
    public static string SelfTest()
    {
        int passed = 0, total = 0;
        var fails = new System.Text.StringBuilder();
        Action<string, bool> check = (name, ok) =>
        {
            total++;
            if (ok) passed++;
            else fails.Append(" [").Append(name).Append(']');
        };

        // A record: current position at +0x20, destination at +0x2A8.
        var buf = new byte[0x800];
        WriteTriple(buf, 0x20, 10f, 20f, 38f);       // where it is
        WriteTriple(buf, 0x2A8, 60f, 95f, 41f);      // where it is going

        List<int> hits = FindCandidates(buf, 10.0, 20.0, 60.0, 95.0, 1.0);
        check("destFound", hits.Contains(0x2A8));
        check("destPositionExcluded", !hits.Contains(0x20));
        check("destOnlyOne", hits.Count == 1);

        // A record that only holds the position must yield nothing.
        var only = new byte[0x800];
        WriteTriple(only, 0x20, 60f, 95f, 41f);
        check("destEmptyWhenNoField", FindCandidates(only, 10.0, 20.0, 60.0, 95.0, 1.0).Count == 1
              && FindCandidates(only, 60.0, 95.0, 60.0, 95.0, 1.0).Count == 0);

        // Unaligned and NaN-filled buffers must not throw or invent hits.
        check("destIgnoresNan", FindCandidates(FillNaN(new byte[0x100]), 0, 0, 50, 50, 1.0).Count == 0);

        if (fails.Length == 0) return string.Format("DestHunt self-test {0}/{1} passed", passed, total);
        return string.Format("DestHunt self-test {0}/{1} FAILED:{2}", passed, total, fails);
    }

    private static void WriteTriple(byte[] buf, int at, float x, float y, float z)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(x), 0, buf, at, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(y), 0, buf, at + 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(z), 0, buf, at + 8, 4);
    }

    private static byte[] FillNaN(byte[] buf)
    {
        for (int i = 0; i + 4 <= buf.Length; i += 4)
            Buffer.BlockCopy(BitConverter.GetBytes(float.NaN), 0, buf, i, 4);
        return buf;
    }
}
