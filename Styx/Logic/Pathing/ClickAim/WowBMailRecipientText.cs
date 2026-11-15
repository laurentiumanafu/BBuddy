using System;

namespace Styx.Logic.Pathing;

// Supervised diagnostic only. A posted character is not evidence of focus,
// field contents, a valid recipient, or permission to send mail.
internal static class WowBMailRecipientText
{
    internal static bool ValidForTest(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        foreach (char c in name)
            if (!char.IsLetter(c) && c != '-') return false;
        return true;
    }

    // One pass only, no clearing, retries or submit. A failed/partial run must
    // be inspected and cleared manually. Each UTF-16 char must be a letter or
    // hyphen (surrogates, controls and combining characters are rejected).
    internal static bool TryPost(string name, Func<bool> safe, Func<char, bool> post, Action pause, out int posted)
    {
        posted = 0;
        if (!ValidForTest(name)) return false;
        foreach (char c in name)
        {
            if (!safe() || !post(c)) return false;
            posted++;
            pause();
        }
        return true;
    }
}
