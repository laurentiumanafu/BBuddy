# Seal of the Crusader buff (experimental 70707/70862/70888/70938)

Place a Seal of the Crusader rank in a normal first-action-bar slot 1–12 with its normal key
binding. Its Spells role defaults to **Buff**; set it to Ignore to disable it. In Combat
settings, enable **Use buffs** and **Use buffs in combat**. The bot then presses that key
during a fight when the seal's buff is not present, subject to the same guards as the other
combat buffs.

Managed ranks and their estimated mana costs (WoW Forever trainer data, not client state):

| Rank | Spell ID | Mana |
| --- | --- | --- |
| 1 | 21082 | 25 |
| 2 | 20162 | 40 |
| 3 | 20305 | 65 |
| 4 | 20306 | 90 |
| 5 | 20307 | 125 |
| 6 | 20308 | 160 |

## What is checked

1. The seal is on the bar as an eligible Buff (not Ignore/Damage) and has a bound key.
2. The bot is in combat, on the same player/map/session, not a ghost, with a stable target and
   no STOP request.
3. The experimental inline aura candidate does **not** contain any Seal of the Crusader rank.
4. Estimated mana: power type is mana and current power is at least the rank's estimate.

If several Crusader ranks are on the bar, only the highest eligible rank is pressed. Any
Crusader rank counts as present, so an active rank 1 suppresses a rank 6 on the bar instead of
replacing it.

Seals are **combat-only**, like the ground totems: they are never pressed by the out-of-combat
30-minute buff timer. An attempt is limited to once per 10 seconds per spell ID.

## Seal exclusivity

The client keeps only one Seal on a Paladin, and casting one cancels another. If a different
recognized Seal is active **and** that seal is itself an eligible Buff on the bar, the bot does
not replace it, to avoid two keys cancelling each other; it logs the reason. An active seal the
bot does not manage (a rank set to Ignore, or a seal not on the bar) is replaced.

Recognized other-seal IDs are only a guard for published Paladin seals: Seal of Righteousness
(20154, 20287–20293, 21084), Seal of Justice/Light/Wisdom (20164, 20165, 20347–20349, 20166,
20356, 20357), Seal of Command (20375, 20915, 20918–20920) and Seal of Martyrdom (407798).
No duration or cost is inferred from these; a wrong or missing ID only means the guard does not
fire.

## Evidence and limits

The aura presence check is the same experimental inline candidate used for the Shaman buffs:
a read-only bounded read at `player+0x5F0` requiring the inline pointer (`player+0x608`), the
16-tagged capacity word, a stable client clock and agreement between the occupied slots and the
active-count word. IDs are read at `player+0x6F0 + slot * 0x128` with the duration and expiry
words at `+12` and `+16`.

One 70938 Paladin trace supplied by the user showed a nine-slot layout with six occupied, and
the seal appearing at slot 6 about five seconds into the recording with a 30000 ms duration
word, while a second trace without the seal never contained the ID. Observed container lengths
are therefore 9, 11, 12, 14 and 15; the scan is bounded by the tagged capacity of 16 and keeps
requiring occupancy agreement. The length word's meaning is still unproven, and the layout has
not been confirmed on other builds or classes.

A key press is not a cast: the client's own mana, cooldown, range, GCD and Seal rules apply, and
the bot cannot read any of them here. Costs are estimates and the aura candidate can be
unavailable, in which case the seal is withheld rather than assumed missing. This has not been
tested in the game or built on Windows.
