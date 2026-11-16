using System;
using System.Collections.Generic;
using Styx.WoWInternals;

namespace Styx.Logic.Pathing;

// Hover-only: identify the user-recorded full GUID, never select or click.
// A changed camera or standing position can move the mailbox away from the
// recorded pixel; an exact GUID match is still only an experimental candidate.
internal static class WowBMailboxHoverSearch
{
    internal static bool Find(int width, int height, int centerX, int centerY, string expected,
        Func<int, int, bool> move, Func<(bool Readable, WowBGuid Guid)> read,
        Func<bool> safe, Func<int, bool> wait, out int x, out int y, out string reason)
    {
        x = y = 0;
        reason = "invalid search geometry or recorded candidate";
        if (width < 200 || height < 200 || centerX <= 0 || centerX >= width - 1 ||
            centerY <= 0 || centerY >= height - 1 || string.IsNullOrWhiteSpace(expected)) return false;
        reason = "hover-away move, safety or read failed";
        if (!safe() || !move(Math.Max(1, width / 20), Math.Max(1, height / 20)) || wait(200) || !safe()) return false;
        var away = read();
        if (!away.Readable) { reason = "hover-away candidate unreadable"; return false; }
        if (wait(50) || !safe()) { reason = "hover-away confirmation interrupted"; return false; }
        var awayAgain = read();
        if (!awayAgain.Readable || awayAgain.Guid != away.Guid) { reason = "hover-away candidate unstable or unreadable"; return false; }
        if (!away.Guid.IsEmpty && away.Guid.ToString() == expected)
        { reason = "hover-away still matches recorded candidate; no fresh transition"; return false; }

        var points = new List<(int X, int Y)>();
        points.Add((centerX, centerY));
        foreach (int radius in new[] { 40, 80, 140, 220, 320 })
            for (int step = 0; step < 8; step++)
            {
                double a = Math.PI * 2 * step / 8;
                points.Add((centerX + (int)Math.Round(Math.Cos(a) * radius), centerY + (int)Math.Round(Math.Sin(a) * radius)));
            }
        // Coarse full-client fallback, not a guarantee of finding a small or
        // obscured object. Sampling only moves the virtual cursor, never clicks.
        for (int row = 0; row < 6; row++)
            for (int col = 0; col < 8; col++)
                points.Add(((int)Math.Round((col + 0.5) * width / 8.0), (int)Math.Round((row + 0.5) * height / 6.0)));
        int probes = 0, empty = 0, different = 0;
        string recordedPoint = "not sampled", recordedGuid = "unavailable";
        foreach (var point in points)
        {
            if (point.X <= 0 || point.Y <= 0 || point.X >= width - 1 || point.Y >= height - 1) continue;
            reason = "hover search interrupted or move failed";
            if (!safe() || !move(point.X, point.Y) || wait(200) || !safe()) return false;
            probes++;
            var current = read();
            if (!current.Readable) { reason = "hover candidate unreadable after probe " + probes; return false; }
            if (probes == 1)
            {
                recordedGuid = current.Guid.ToString();
                recordedPoint = current.Guid.IsEmpty ? "empty" : recordedGuid == expected ? "matched" : "different";
            }
            if (current.Guid.IsEmpty) { empty++; continue; }
            if (current.Guid.ToString() != expected) { different++; continue; }
            if (wait(50) || !safe()) { reason = "hover confirmation interrupted"; return false; }
            var again = read();
            if (!again.Readable || again.Guid != current.Guid)
            { reason = "matching candidate changed or became unreadable during confirmation"; return false; }
            x = point.X; y = point.Y;
            reason = "recorded candidate matched twice after hover-away on probe " + probes + "; mailbox identity and UI remain unverified";
            return true;
        }
        reason = probes + " hover-only probes: recorded point " + recordedPoint + " (observed " + recordedGuid +
            ", expected " + expected + "), " + empty + " empty, " + different + " other candidates; recorded candidate not found";
        return false;
    }
}
