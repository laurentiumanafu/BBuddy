# WowBClickAim — click-to-move by measurement, with a closed loop

## This tool is the bench; the bot is the target

The movement now lives in the bot as well: `Styx/Logic/Pathing/ClickAim/` in the
CopilotBuddy tree implements `IPlayerMover` with the same measured model, and reads
the same `clickaim-cal.txt` this tool writes. See `CLICKAIM_PORT.md` at the repo
root. Nothing about the calibration changes: run this tool once on the client, then
the bot reuses the file. The differences in the port are tick-driven dispatch,
distance-banded click heights (a path waypoint is not a goal), and turn legs that
advance the yaw by the model instead of by the walk.

## v15 — the turn legs were feeding the planner a heading that is not a camera

Two field runs at 15:56/15:57 on the same client (pid 3772), both against an
obstructed spot:

```
25 180   24 legs, no convergence, ended 13.42 yd from a 25 yd goal; 1 pass-by stop
100 0     5 legs took it from 100.00 to 21.20 yd, then 19 legs thrashed
```

Those first five legs of the 100 yd run are the best long approach yet - 21.54,
17.92, 21.51, 45.75, 23.82 yd on plain `click=+225 dy=-140` legs, no drags at all.
Then both runs fall apart into drag legs, and the reason is a bug, not terrain:

**A drag leg does not walk along the camera it turned to.** The right button is
held down for the drag, so the character *runs along its old facing* while the
camera rotates; only the click afterwards walks on the new facing. The leg's
displacement is the sum of the two, and its heading belongs to neither camera.
The walker took that heading as the next `headingAtZero`, so the yaw estimate
swung 172 deg across the 25 yd run, the goal never re-entered the +/-75 deg click
cone, and the planner asked for another drag - 24 legs, no arrival.

Turn legs now advance the yaw by the model (`degPerDragPx * dragPx`, the same
slope the drag was planned from) and say so in the log; a pure click leg still
measures the yaw directly, which is what keeps the model honest.

What the 100 yd run also showed, in its favour: `WANDER_MAX_YD` never tripped (the
longest walk was 89.25 yd in 12.84 s), the pass-by stop fired for the first time
("passed the goal at 4.42 yd; cancelled the walk rather than run on"), and the
`HEADING_MIN_WALK` guard announced itself instead of silently poisoning the yaw.

## Profiles and hotspots: what this walker can and cannot reach

Measured on `Ashenvale (22-24).xml` (44 hotspots, 10 quests):

| hop | distance | verdict with this tool |
|---|---|---|
| hotspot to hotspot, same cluster | 6-97 yd, typically 27-60 | **yes** - 40 yd took 2 legs, 78 yd took 5 |
| the wide cluster (Zoram Strand) | 97-269 yd | marginal: 3-10 legs each, needs open ground |
| quest centroid to quest centroid | 276-1485 yd | **no** - 24-leg cap, no routing |
| player to nearest hotspot in that file | 448-2837 yd | **no** |
| `<RunTo>` to a boat/portal/taxi | other continent | **no** - no transport logic at all |

Three structural limits, independent of aiming:

1. **Reach.** Every leg is one click that the client resolves itself, and the fit
   is measured over +/-300 px, so a leg is 5-45 yd. A run is capped at `MAX_LEGS 24`.
2. **No route.** The walker aims straight at the goal and detours blindly; there is
   no navmesh, no roads, no waypoints. Anything with a wall, a cliff, water or a
   cave between the two points fails, and the profile's points are frequently
   inside towers and ruins.
3. **No Z.** Hotspots carry Z and the tool ignores it. Within one cluster the spread
   is 2.9-12.8 yd - small enough to ignore on open ground, fatal for a hotspot on a
   tower floor or in a cave.

So: one `--goto X Y` at a time, within a hotspot cluster, on ground you can see -
yes, that is what v15 is for. A profile as written (cross-zone `RunTo`, transports,
`LoadProfile` chains, vendors, turn-ins) is not walkable with this procedure, and no
amount of click tuning changes that - it needs a waypoint router and transport
handling, which is the same navmesh work that is already gated on the client's
map-data layout.

## v14 — one perfect run, one false refusal, one circle

Three field runs, in the order they happened:

```
15:43  --load --walk 100 0    no cal file yet -> full calibration, 24 legs, 65.21 yd left, converged=0
15:45  --load --walk  25 180  "click walked 56.09 yd and never came to rest" -> refused to walk
15:45  --load --walk  40 270  2 legs, remaining 0.77 yd, drift -0 %/-0 %
```

The 40 yd run is the best execution so far: five-and-a-half minutes of calibration
replaced by one click, then leg 1 walked 34.72 yd of the 40 and leg 2 finished it.
Two legs for 40 yd.

The other two each exposed a real defect.

**The 25 yd run refused for no reason.** Its single centre click walked 56.09 yd at
6.9 yd/s - a perfectly normal far click - and needed 8.1 s to finish. v11 had cut
the rest wait to 8 s, so the tool called it "never came to rest" and refused to
walk. A click can legitimately mean 100 yd. The wait is now **20 s**, and what makes
a runaway a runaway is **distance**: `WANDER_MAX_YD 120` of continuous travel in one
wait, which still catches v9's 171.52 yd and no longer punishes a 56 yd click.

**The 100 yd run circled at 40 yd from the goal for a dozen legs.** Two causes:

* it re-measured the camera yaw after **every** walk, including 0.63 and 1.35 yd
  walks whose heading is mostly noise. With a wrong yaw the planner kept deciding
  the goal was outside the click cone and asked for another 45 deg drag, which
  rotated the wrong way, which made the next yaw worse. The yaw is now only taken
  from walks of **3 yd or more**; shorter legs say so and keep the old one.
* it only ever detoured when a leg moved under 0.5 yd. Legs of 5-13 yd that gained
  nothing were treated as progress. There is now a second signal - **three legs in
  a row with no net gain** - that triggers the same alternating detour.

Also: a turn leg now plans the **far** click (when the goal is more than 30 yd out),
because the walk after a turn is what re-measures the yaw, and the 45 deg cap meant
a turn leg's short hop could never do that. And the "steeper at the edge" note was
comparing a magnitude with a signed slope, so it printed `9.78 vs -10.23`.

## v13 — two converged runs, and the panic that fired on nothing

Both field runs today reached their goals:

```
--load --walk 60 0    5 legs   remaining 0.79 yd   drift click +16 %
--load --walk 30 90  14 legs   remaining 0.86 yd   drift click +12 %, drag +17 %
```

The 60 yd run is the best this tool has done: five legs, under a yard, having
loaded its calibration instead of spending four minutes measuring it. The 30 yd run
took the long way because its spot was obstructed - it circled, got a 99.35 yd walk
that did not help, and then walked back and converged anyway.

Five things in those two logs were wrong, and one of them mattered:

1. **The runaway panic fired on a legitimate click.** Leg 5 was a planned `dy=-200`
   click - the tool's own "no progress" nudge - which correctly walked 99.35 yd
   toward a point ~100 yd away, and v12 called it a stuck button and panicked. A
   long leg is not a runaway. The real signature is a walk **still moving when the
   rest wait gives up**, or a **near hop that travels like a far click**: a dy=-60
   click means about 10 yd, so 40+ yd from it is a button left down (that is v9's
   171.52 yd case, still caught). The threshold now depends on the leg's own dy
   and the message reports both numbers.
2. **The nudge must not change the distance.** It exists to sample different
   terrain under the cursor, not to send the character 100 yd away; the cycle is now
   `{-70, -90}` from the preferred near-far pair instead of `{-200, -70}`.
3. **`--load` printed two contradictory messages** when no file existed ("no file
   there yet" followed by "missing or unreadable"). The second is gone.
4. **A single saved sample is not a calibration.** The 60 yd run loaded `-0.08631`
   and immediately measured `-0.0934`, then `-0.10017` (+16 %). Every converged run
   now appends the model it learned, so `--load` pools several real walks instead of
   reusing whichever line was written last.
5. **The fit is a local slope, not a straight line**, and the log now says so: both
   calibrations steepen toward the edge (0.86 deg/px near 150 px, 1.08 near 300).
   Plans are therefore capped at **225 px**, inside the range the fit was checked
   over, and the next leg takes the remainder.

Also fixed: the footer printed `legs=15` after `REACHED after 14 legs`.

## v12 — it converged, and the log that proved it was mislabelled

The first fully successful run:

```
click fit: median of 3 of 3 probes, -0.12674 deg/px
  holdout dx=+300: measured -33.97, model -38.02, error +4.05
  holdout dx=-300: measured +36.52, model +38.02, error -1.50
  both held-out offsets are predicted: click sensitivity VERIFIED
cal[drag] +200 -> -0.14557, -200 -> -0.22124, +300 -> -0.12315, -300 -> -0.22488
  drag fit: median of 4 of 4 probes, -0.17871 deg per pixel
...
leg  7: click= -243 dy=-140 -> moved 19.29 yd, remaining 22.08 -> 5.05
      (capture stop at 1.79 yd, coasted 0.18 yd; too little to learn a stop distance)
leg  8: click= -300 dy= -60 -> moved  4.43 yd, remaining  5.05 -> 1.61 (stop)
REACHED: remaining 1.61 yd after 8 legs
drift vs calibration: click -1%, drag +2%
```

Eight legs for 30 yd, a verified model, a stable slope through the run, and a stop
that fired and was correctly *not* learned from (0.18 yd of coast is a character
already slowing, not a reaction time). Compare with 17 legs in v6.

Four things still needed fixing, and one of them is embarrassing:

1. **The banner said v9 while the build was v10.** The version string was typed
   separately from the changes and was never bumped. It is now one `AIM_VERSION`
   macro used by the banner, so the log cannot disagree with the binary again.
2. **`--load` did not say where it looked.** It printed "missing or unreadable",
   which was true and useless. It now prints the path, says when the file does not
   exist yet, and pools every calibration the file contains.
3. **The slope is a population, not a constant.** Two verified runs measured
   -0.10709 and -0.12674 deg/px - 18 % apart - so the file now appends one line per
   calibration and `--load` uses a robust pooled median, reporting the spread when
   it is over 25 %. Trusting the last-written line was the same single-sample
   mistake this tool has already made four times.
4. **One drag can overshoot.** Leg 4 asked for 420 px (the cap) and ran 37.10 yd
   *away* from the goal, 23.47 -> 38.49. A single drag above **45 deg** is now
   split across legs: the first leg turns part of the way and clicks, the next leg
   turns the rest. Detours keep their 55 deg because they are deliberate.

## v11 — the calibration is finally verified; the failure moved to the input state

This is the first run where the camera model passed its own test:

```
click fit: median of 4 of 4 probes, -0.10709 deg/px  (-10.71 deg per 100 px)
  probe dx=+150: measured -14.69, model -16.06, error +1.37 deg
  probe dx=-150: measured +15.70, model +16.06, error -0.36 deg
  probe dx=+225: measured -23.48, model -24.09, error +0.62 deg
  probe dx=-225: measured +27.31, model +24.09, error +3.22 deg
  holdout dx=+300: measured -39.39, model -32.13, error -7.26
  holdout dx=-300: measured +31.15, model +32.13, error -0.97
  both held-out offsets are predicted: click sensitivity VERIFIED
cal[drag] +200 -> -0.22500, -200 -> -0.10418, +300 -> -0.22494 deg/px
  drag fit: median of 3 of 3 probes, -0.18471 deg per pixel
```

Residuals under 3.3 deg on four probes and both held-out offsets inside tolerance.
Four runs of work on this channel, and it is now measured rather than fitted.

Then:

```
leg  6: drag= -357 click= +0 dy= -60 -> moved 171.52 yd in 25.00 s, remaining 27.67 -> 174.61
  [between legs] still moving after 20000 ms; using current position
leg  7: ... remaining 308.85 -> 348.88
leg 10: ... remaining 371.10 -> 342.61
leg 22: ... remaining 104.29 ->  93.18
leg 23: ... moved 0.34 yd   (stuck, 93 yd from a 30 yd goal)
```

A leg that covers 171.52 yd at 6.9 yd/s is a character **running**, not following a
click, and it kept running for another 20 s after the leg was declared over. The
client was left with the **right mouse button down** - a held right button means
"run toward the cursor" - because a `WM_RBUTTONUP` was dropped. Every later leg then
planned from a position 340 yd off, and fifteen legs were spent walking back.

v11:

1. **The release is sent twice**, in both directions of the drag.
2. **PANIC STOP**: on a runaway, release the right button twice, move the cursor to
   the centre, click once (left), tap the stop key, and wait for the character to
   settle. Clicks cannot cancel a held button (they are read as its direction), so
   this had to be a separate sequence.
3. **The rest wait is 8 s, not 20.** v9 waited twenty seconds while the character
   ran, then carried on from wherever it had got to.
4. **Runaway is detected in two places**: at the top of a leg (still moving between
   legs) and after a click (a leg over 40 yd). Two runaways, or a goal distance that
   has grown 3x, ends the run with an explanation instead of a 15-leg walk back.
5. **No heading is taken from a zero-length walk.** v9 printed `heading at centre
   right now = 0.0 deg` because a probe that moved 0.00 yd was marked "clean" and
   `heading_of` on two identical points returns 0.0. `probe_click` now requires
   0.5 yd of travel before its heading counts.
6. **Click offsets are capped at 300 px**, the range the model was measured over
   (v9 asked for -584 px; the response saturates past 225).
7. **A skipped drag probe re-anchors the heading** with one click at a different
   height, and if it cannot, the *next* drag probe is discarded rather than being
   measured against a stale reference - which is exactly what produced the -0.10418
   outlier above.

What the same run also proves: legs 11-22 walked 240 yd back toward the goal on
plain centre clicks with no drags at all. The click channel works; the failure was
the input state, not the model.

## v10 — a calibration that found no turn channel, and then poisoned the next run

Two runs of v9, same pid, same window. The click calibration worked in the first
one — four probes, slopes -0.069, -0.083, -0.091, -0.071, median **-0.07861
deg/px**, residuals under 3 deg — and then everything after it collapsed:

```
cal[3] click dx=0 dy=-200 -> 0.43 yd
cal[4] click dx=0 dy=-20  -> 1.96 yd
cal[drag]  +200 px: the click after the drag walked 0.07 yd ... (probe skipped)
cal[drag]  -200 px: ... 1.12 yd (skipped)
cal[drag]  +300 px: ... 2.90 yd (skipped)
cal[drag]  -300 px: ... 0.99 yd (skipped)
cal[key] RIGHT 500 ms -> the click afterwards walked 3.41 yd; unmeasurable
WARNING: neither turn channel moved the camera.
Cannot plan a leg: the goal heading is 90.0 deg, heading at centre is -30.5 deg
```

Five separate defects, in order of how much they cost:

1. **Calibration walked the character 230 yd in random directions.** Every probe
   walks wherever its click lands, and the click height was `dy=-140`, i.e. 20-70 yd
   per probe. By the drag probes the character was jammed somewhere that every
   click moved it under 3 yd. Probes now use `dy=-100` (about 6-10 yd) and the
   minimum walk is 6 yd instead of 10, so the whole calibration wanders a fraction
   as far.
2. **A turn only needs a clean walk, not a long one.** Requiring 8 yd threw away
   *all four* drag probes and both key probes - 3.41 and 6.37 yd were perfectly
   good. `CAL_MIN_TURN_WALK` is now 2.5 yd. That single number is the difference
   between "no turn channel" and a walkable bot.
3. **A failure was saved and then loaded.** The run wrote
   `degPerDragPx=0 degPerKeyMs=0` to the calibration file, and the `--load` run
   read those zeros, skipped the turn probes, and refused to plan at all - it moved
   0.00 yd. Now: a calibration without a turn channel is **not saved**, and `--load`
   treats a zero channel as "unknown", probes it, and says so.
4. **A goal behind the camera is not a dead end.** The walker broke out with
   "cannot plan a leg" when the goal was outside the cone and no turn channel
   existed. But the camera follows the character, so walking at the **edge of the
   cone** leaves the next leg starting from a heading closer to the goal: the bot
   can turn by walking. New `REACH-AROUND` legs (up to 8) do exactly that.
5. **"The character is not walking" was reported as a camera failure.** If two
   probes in a row move under 2 yd, the run now stops and names the likely causes:
   dead, rooted or in combat, inside geometry, or clicking under a roof.

The two `dy spread` evidence clicks are also gone from the default path
(`--dyevidence` brings them back): they cost two walks - one of them 114 yd - and
they ran *before* the turn probes, using up the character's position for nothing.

## v9 — the world has obstacles, and two of v8's numbers were lies

The first v8 field run ended `BLOCKED ... remaining 13.79 yd` after 7 legs. Three
things in that log are defects, and one is the environment.

**1. `leg 1: moved 0.00 yd in 0.25 s`** — not a blocked click. The rest detector
needed 4 still samples at 60 ms, and it started counting the instant the click was
posted, before the client had begun walking. So "the character is standing still"
and "the click never took" were the same reading, and a working click was recorded
as a blocked one. The detector is now armed only 900 ms after the click, or as soon
as the character actually moves.

**2. `leg 3: IMPOSSIBLE GEOMETRY - moved 50.19 yd in 15.70 s`** — that is 3.2 yd/s,
a perfectly ordinary run. It was rejected because a whole leg was being judged by
`am_plausible_step`, which clamps `dt` at 3 s and therefore allows at most 45 yd -
the clamp exists for *per-poll* steps, not for legs. `am_plausible_leg` now judges a
leg by average speed, and the one leg that was making progress is no longer thrown
away.

**3. The drag channel was a single sample again.** `cal[drag] -200 px: the click
after the drag walked 4.78 yd, so the turn it cannot be measured`, and then
`only one direction was measurable: drag channel -0.19706 deg/px` - and legs 1 and 2
were planned with `drag=+532` and `+536` px off that one probe. The drag channel now
gets exactly what the click channel got in v8: **four probes** (+200, -200, +300,
-300), each measured against the heading of the walk that preceded it, median with
2x outlier rejection, and the unexplained leftover reported as a bias. If only one
direction can be measured it says so and **caps per-leg drags at 420 px**, because a
saturated drag is free to be anything.

**4. The environment: obstacles are the normal case, not an error.** Legs 4-7 in
that run answered "two legs made no progress" with *the same heading and a slightly
different click point* - four pushes into the same hillside - and then declared the
goal walled in. A blocked leg is a blocked **heading**, not a blocked goal. So after
two short legs the next leg is a **detour**: turn 55° off the goal bearing, far-click,
walk the tangent, and plan again from the new position. The side alternates
(`am_detour_side`), six detours maximum, and only then does it say `BLOCKED` in a
form that means something ("the goal is walled in, under water, or up a slope this
client will not walk").

**5. Calibration is now reusable.** Slopes are a property of the client and the
camera settings, not of a run, so they are written to
`C:\ghidra-scripts\clickaim-cal.txt` and `--load` reuses them - one click to find
the camera yaw, then walking. The ~4 minute probe sequence is for when something has
changed, not for every attempt.

What this does **not** fix: monsters. There is no combat in this tool; if something
is hitting the character, movement stops and no amount of detouring helps. It will
report the leg as short and try to walk around, which is all a click synthesiser can
do.

## v8 — the camera moves between probes, so the reference has to move too

v7 refused to walk, and it was right to: both held-out offsets missed by 12-19 deg.
The reason is in its own probe list. Six probes, one fixed reference:

| dx | heading | turn vs start | turn vs previous walk | slope vs previous |
|---:|---:|---:|---:|---:|
| 0 | 47.0 | — | — | — |
| +150 | 34.1 | −12.90 | −12.90 | −0.08600 |
| +300 | 13.1 | −33.90 | −21.00 | −0.07000 |
| −150 | 34.4 | −12.60 | +21.30 | −0.14200 |
| −300 | 46.0 | −1.00 | +11.60 | −0.03867 |
| +225 | 22.1 | −24.90 | −23.90 | −0.10622 |
| −225 | 41.3 | −5.70 | +19.20 | −0.08533 |

Against the fixed start heading, three of the six probes have the **wrong sign** -
a negative offset cannot turn the camera the same way as a positive one. Against
each probe's *predecessor*, all six agree in sign and in rough size. The client did
not change between probes; the camera did. **WoW's follow camera swings back
behind the character while it runs**, so the yaw at probe *k* is the heading of the
walk probe *k−1* produced.

That single fact explains everything in this file's history:

* v6's −0.36086 deg/px: a fixed origin plus one 4.60 yd walk whose camera had not
  re-centred, divided by 300.
* v7's rejected holdouts: correct data, wrong reference.
* The "same offset, different distance" notes: `dy` is not the only thing terrain
  decides, the camera has also rotated since the last measurement.

v8 therefore:

1. **Measures every probe against the previous walk.** One slope per probe, then
   the **median** with 2x outlier rejection (`am_median_slope`), not a least-squares
   line through one origin. Six probes give −0.0857 deg/px; the surviving mean of
   the four inliers is what gets used.
2. **Carries the same rule into the walk loop.** `headingAtZero` is now the
   measured heading of the last leg, not `walkedHeading − degPerPx*clickPx`. v5 and
   v6 back-solved it from the click model, which imported the model's own error
   into the next leg's reference.
3. **Never refuses to walk when a slope exists.** If the held-out offsets miss, the
   slope is labelled **UNVERIFIED** and the closed loop corrects it leg by leg -
   because the response is *not* linear over ±300 px (it saturates: ±225 and ±300
   buy about the same angle). v7's all-or-nothing gate was honest and useless.
4. **Still refuses when there is nothing to model**: fewer than two usable probes,
   or a ±300 px span outside 30-150 deg.
5. Keeps v7's pass-by stop, `closest=` telemetry and gated coast learning.

Calibration is still ~2 minutes, but it drops the separate turn-baseline walk
(one less unmeasured rotation) and re-uses the last probe's heading.

## v7 — the camera model was fitted to one walk, and that walk was not a measurement

v6 arrived (1.63 yd, `converged=1`) and still used 17 legs to cover 30 yards. The
report says why, and it is not the planner: **click sensitivity was −0.36086
deg/px**, against −0.10983 two runs earlier, on the same client, same window mode,
same machine. Every planned heading was therefore wrong by tens of degrees, and
the character ran diagonally past the goal: legs 5, 8 and 13 *increased*
`remaining` (9.70 → 18.68, 2.72 → 21.38, 17.06 → 20.10 yd) while moving 20-35 yd.

Both numbers came from **one click at dx=+300**. One long walk cannot carry a
camera model. A walk bends after four yards of terrain, and a walk that never
comes to rest has no heading at all - v6 measured `cal[2]` from a 4.60 yd walk
and `cal[1]` from a 27.78 yd one, and divided the difference by 300.

v7 replaces that with:

1. **A fit, not a sample.** Four click offsets (+150, +300, −150, −300), least
   squares through the centre click, only walks that both came to rest and
   travelled at least 10 yd. Each sample is printed with its residual.
2. **Held-out falsification.** Two offsets the fit never saw (±225) are measured
   afterwards. If either falls outside `max(3°, 35 %)` the tool **refuses to
   walk** and says so, instead of steering with a curve fitted to noise. A
   ±300 px span that implies less than 30° or more than 150° of camera turn is
   rejected the same way.
3. **Two drags, opposite directions.** One drag cannot separate "the drag turned
   the camera" from "the character was already walking at another angle". v6's
   single +200 px probe reported +0.04202 deg/px where the two runs before agreed
   on −0.23. Both signs give a slope *and* a direction bias, and the bias is the
   number the single probe was hiding.
4. **A pass-by stop.** A leg that comes within 6 yd of the goal and then moves
   0.5 yd further away for three polls is cancelled at the closest approach. That
   is what the 2.72 → 21.38 yd legs needed: the walk was never going to come
   back, and re-planning from 21 yd away looks like a broken model when it is
   only a lateral miss.
5. **`closest=` on every leg line**, plus `stop` / `pass-by stop` / `no stop`.
   A leg that ends further away having passed 3 yd from the goal is a completely
   different event from one that never went near it, and until now the report
   could not tell them apart.
6. **The coast is only learned from a real coast.** v6 learned `stopDistance`
   from the single stop it got, which coasted 0.07 yd - a character already
   slowing - and pulled the estimate 0.80 → 0.58 yd on no evidence. Samples under
   0.30 yd are now ignored and the report says why.

Calibration now costs about two minutes and six long walks. That is the price of
a model that can be wrong out loud instead of quietly.

## v6 — it arrives, but it cannot stop, so it walks past

The v5 field run converged in **6 legs** (17 before) with **no impossible legs** and
drift of **+3% click / +6% drag** against calibration. The validation and the
weighted learning did their job.

The remaining weakness is the *end* of the walk:

```
leg 1: moved 11.55 yd, remaining 30.00 -> 18.45
leg 2: moved 20.92 yd, remaining 18.45 ->  4.10
leg 3: moved 10.09 yd, remaining  4.10 ->  6.20     <- walked 10 yd to close 4
leg 4: moved  7.36 yd, remaining  6.20 ->  3.28
leg 5: moved 10.85 yd, remaining  3.28 ->  8.08     <- walked 10 yd, got further away
leg 6: moved  9.34 yd, remaining  8.08 ->  1.68
REACHED
```

Four legs of 7–11 yd to close a 4 yd gap, one of which made it worse. The click
sets a *direction* and the terrain decides the distance, so the last few yards are
a lottery — v4 needed 17 legs for the same reason and finished 1.68 yd out.

v6 adds the missing half of the primitive: **stop**.

* while the character runs, the position is polled every 60 ms;
* when the remaining distance is close enough that the stop will land inside the
  capture radius, the walk is cancelled by tapping `W` for 70 ms — a movement key
  press replaces the pathing move, which is the only stop this client exposes;
* the **coast distance is measured**, not assumed: it is how far the character
  travelled between the key press and standstill, smoothed 70/30 into
  `stopDistance`. The first guess is 0.8 yd (deliberately large: stopping early
  costs one short leg, stopping late puts you past the goal);
* `am_should_stop(remaining, coast, capture)` fires at `capture + coast`, so the
  overshoot lands *on* the goal rather than past it;
* if the key does not cancel the walk within 6 s, stops disable themselves and say
  so, rather than silently running every leg to its far end.

The capture rule is unit-tested against a continuous-motion model: at 7 yd/s with
60 ms polling and 0.35 s of latency, a correct coast estimate stops inside the
capture radius from every start distance (3 to 60 yd), and an under-estimated
coast demonstrably overshoots — which is the argument for measuring it.

Report differences you will see:

```
capture: cancel the walk within 1.0 yd of the goal (tap W, 70 ms);
  stop distance starts at 0.80 yd and is measured from the first stop
leg 3: drag=   +0 click=  +229 dy= -70 -> moved 16.56 yd in 2.67 s, remaining 75.50 -> 60.28
      (stop at 1.06 yd, coasted 0.94 yd; stop distance 0.84 yd)
```

`--nostop` restores the old click-and-wait behaviour, `--capture YD` moves the
radius.


## v5 — it converged, but two legs in the log were impossible

The v4 field run **reached the goal**: 30 yd in 17 legs, `remaining 1.74 yd`,
`converged=1`. Drag calibration worked (−0.22653 deg/px) and so did aiming.

Two lines in that log cannot be true:

```
leg  7: drag= -638 click=   +0 dy= -60 -> moved  5.45 yd, remaining   2.04 ->  30.83
leg  8: drag= -781 click=   +0 dy= -60 -> moved  0.63 yd, remaining  30.83 -> 110.23
  [before click] still moving after 20000 ms; using current position
```

For a **fixed** goal, the distance to it can grow by at most the distance walked.
Leg 7 walked 5.45 yd and the goal got 28.79 yd further away; leg 8 walked 0.63 yd
and it got 79.4 yd further away. So those position reads were not the character's
position — the second one arrived right after the 20 s rest timeout. Everything
after leg 9 is consistent again, which is why the run still converged.

v5 makes that detectable and harmless:

* **one read per leg.** The leg is planned from the position it is measured from
  (v4 planned from an earlier read, so a stale sample silently invalidated the
  plan). `rest_click` takes that sample as input instead of re-reading.
* **triangle-inequality check on every leg** (`am_leg_consistent`) and a speed
  check on single samples (`am_plausible_step`, 1 yd + 12 yd/s). A leg that fails
  is reported as `IMPOSSIBLE GEOMETRY`, takes no learning, and is re-planned from
  a fresh read; five of them stop the walk.
* **`wait_rest` re-baselines on a discontinuous sample** instead of treating it as
  movement, and no longer tests the rest threshold against a garbage step.
* **learning only from clean legs**: both rests must have settled, travel must be
  3–22 yd (beyond 22 the chord has been bent by terrain), the candidate must be
  within 2x of the calibrated slope, and the update is **weighted by distance** —
  a 21 yd leg moves the model more than 4 times as much as a 4 yd one. The v4 run
  drifted from −0.1017 to −0.1817 (78%) because a 4.3 yd leg counted as much as a
  21 yd one.
* the exit report now prints the drift versus calibration, so a model that wanders
  is visible rather than hidden behind a converged walk.

Also: the short-hop threshold for near-goal legs moved from 8 to 15 yd. Leg 2 in
the v4 run overshot by 20 yd (9.21 → 21.16 remaining) using the long hop.


## v4 — v3 could not plan a single leg; both turn channels are now measured

The v3 build calibrated and then died immediately:

```
WALK: from (10595.89, 866.11) to (10595.89, 896.11), 30.0 yd, tolerance 2.0 yd
Cannot plan a leg (goal outside the cone and no drag sensitivity).
final position (10595.89, 866.11) remaining 30.00 yd, legs=1 converged=0
model at exit: headingAtZero=0.0 degPerPx=-0.07861 degPerDragPx=0.00000
```

Three faults, all mine, all visible in that one line of model output:

1. **`degPerDragPx=0`** — v3 removed the distance model and took the drag
   calibration with it. With no drag slope there is no way to rotate the camera.
2. **`coneHalfDeg` was left at 0** — the planner treats every goal as "outside the
   cone", so even a goal straight ahead was unplannable.
3. **`headingAtZero=0.0`** — never assigned. Even with the other two fixed, the
   first leg would have planned against due east.

v4 measures everything the planner reads:

* **both turn channels are probed.** A 200 px right-drag, then a 500 ms hold of
  `VK_RIGHT`, then `VK_LEFT`, each followed by one measured centre click. A
  channel counts only if it turned the heading by more than 2 degrees; the drag
  probe stays under half a turn on purpose, because at the measured 0.37 deg/px a
  500 px drag would rotate past 180 degrees and alias to the wrong sign.
* **whichever channel moved the camera is the one the planner uses.** A
  background window may not be receiving real mouse deltas at all, in which case
  dragging turns nothing and only the key channel can work.
* **cone = 0.75 × |deg/px| × halfWidth**, capped at 75 degrees — deliberately
  inside the geometric limit, because a click that under-turns costs one leg
  while a click aimed past the edge walks the wrong way.
* `headingAtZero` is set from the last measured centre click of calibration, so
  the walker starts from where the camera actually is.
* `BLOCKED` now requires four **consecutive** non-moves; v3 counted them across
  the whole run and could declare a blocked walk after four unrelated bumps.

Simulated against a client whose distance is terrain-random (not dy-driven):
front goal converged in 10 legs, goal behind the camera in 8 legs with 2 drags,
and — the case that killed v3 — a client where dragging turns nothing, recovered
by the key channel alone in 7 legs.

## v3 — the walker converged; the distance model was the problem

Field run of v2 walked **45.33 yd → 1.93 yd in 14 legs and reported REACHED**.
Click-to-move works end to end. Three corrections came out of that run:

**1. `dy` does not control distance.** v2 fitted `dist = a + b*dy`; it produced
negative predictions, and the legs prove why: every leg used the clamped
`dy=-20`, yet moved between **0.09 and 12.35 yd** (a 150x spread). The click point
is wherever the ray meets the terrain — slope, walls and camera pitch decide, not
the pixel row. v3 deletes the distance model: `dy` is a starting preference and
distance is closed by iteration.

**2. The learning corrupted its own model.** At exit the model reported
`degPerPx=-0.30783` and `degPerDragPx=+0.16937` — 3x off and sign-flipped versus
the calibration (`-0.1029`, `-0.38165`). Cause: a leg that moved 0.09 yd still
reported a "heading", and that noise was fed straight in. v3 only learns from legs
that travel > 3 yd, rejects any estimate whose sign disagrees with calibration or
whose magnitude is more than 3x off, and smooths what it accepts.

**3. What *is* stable.** Across both runs: click sensitivity −0.0936 / −0.1029
deg/px, drag sensitivity −0.3637 / −0.3817 deg/px. The camera model is reliable;
only the distance relationship was not.

v3 also adds `BLOCKED` detection (four clicks under 0.5 yd) and, on two legs of no
progress, moves the click point by changing `dy` — a different pixel lands on
different terrain.

## Result so far

Synthesised right-clicks **work**: 8 of 8 probes moved the character, at full run
speed (~6.8 yd in a 0.85 s window ≈ 7 yd/s). Click-to-move exists on this client
and needs no CTM offset, no projection function, and no injection.

But v1's *numbers* were worthless, and the fault was mine, not the client's:

* every probe reported 6.08–6.87 yd regardless of `dy` from −300 to −60. At 7 yd/s
  that is 0.95 s of running — exactly the sampling window. v1 never waited for the
  character to **stop**, so each probe measured the tail of the previous click
  blended with the start of the next. Distance was saturated by the window.
* the two probes at `dx=0` reported headings −101.9° and −119.0°. A fixed camera
  cannot do that; that disagreement is leftover motion, not camera behaviour.

v2 waits for rest before and after every click, so the number printed for a click
is what that click actually did.

## The limitation that decides the design

A click reaches only the headings **inside the camera cone** — the ray at the
screen edge is at most ~fov/2 off centre. A goal *behind* the camera therefore
cannot be reached by clicking at all: every available click walks the wrong way
and the distance grows. Reaching for the screen edge does not help; it silently
diverges.

So the walker turns the camera with a **horizontal right-drag**, then clicks. The
drag sensitivity is measured, not assumed, and every leg re-learns the model from
what it observed (the walked heading tells you the centre heading, because the
click offset is known).

## Run

Look first (no input synthesised):

```
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --dry
```

Calibrate only, no walking — 7 click probes plus 2 drag probes, ~9 walks (2-3 min):

```
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe"
```

Walk, closed loop, up to 24 legs:

```
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --walk 30 90      (30 yd on heading 90°)
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --goto 10500 1020
"C:\ghidra-scripts\WowBClickAim\WowBClickAim.exe" --probe 300 -140  (one measured click)
```

`--quick` fits the click slope from a single probe with no held-out check. It is
faster and it is exactly how v6 got a slope 3.3x wrong, so it is only for a quick
sanity check of whether clicks move the character at all.

`--sendinput` moves the real cursor instead of `PostMessage`; use it if clicks are
ignored. Hands off the mouse for the whole run — the camera and cursor are the
input channel.

## What the report tells you

Verbatim from the v7 run that motivated v8 (`WowBClickAim-20261029-144250.txt`):

```
click fit: 3 of 4 probes used, -0.05831 deg/px (-5.83 deg per 100 px)
  residual dx=+300: measured  -33.89, fit  -17.49, error -16.40 deg
  residual dx=-300: measured   -1.00, fit  +17.49, error -18.50 deg
  holdout dx=+225: measured  -24.94 deg, model  -13.12, error -11.82 (tol 4.6)  REJECTED
  holdout dx=-225: measured   -5.77 deg, model  +13.12, error -18.89 (tol 4.6)  REJECTED

2 held-out offset(s) fell outside tolerance. The click model does not
predict offsets it was not fitted on, so it is not a model of the camera.
Nothing is walked.
```

Every one of those numbers is real and every conclusion drawn from them was
wrong, because the reference was. v8 prints instead:

```
cal[0] centre click dy=-140 -> 23.51 yd at heading 47.0 deg (camera yaw now)
  dx=+150 turn  -12.90 deg from the previous walk's heading -> -0.08600 deg/px
  dx=-150 turn  +21.30 deg from the previous walk's heading -> -0.14200 deg/px
click fit: median of 4 of 4 probes, -0.08566 deg/px (-8.57 deg per 100 px)
  +/-300 px spans 51.4 deg of camera turn (plausible 30-150)
  holdout dx=+300: measured  -21.00 deg, model  -25.70, error   +4.70
  holdout dx=-300: measured  +11.60 deg, model  +25.70, error  -14.10  OUTSIDE TOLERANCE

  Held-out offsets miss, so this is an UNVERIFIED slope: the response is not
  linear over +/-300 px (it saturates), and the walker will correct it leg by
  leg instead of trusting it.
turn baseline: heading 46.0 deg (from the last calibration walk)
```

Re-running v7's own numbers through this rule gives −0.0857 deg/px, one held-out
offset inside tolerance and one outside - i.e. a slope that is roughly right in the
middle of the range and wrong at its edges. That is exactly the shape a saturating
response predicts, and it is walkable: the loop re-measures every leg.



and then the drag probes, each measured the same way (drag, then a long centre
click, compared against the heading of the walk that preceded the drag).

## Navigation: what a mesh changes, and what it does not

CopilotBuddy and TrinityCore-family bots navigate with a **Recast/Detour mesh**
built from the client's own map data: extract the heightmap and object placements,
rasterise a walkable mesh, path across it, and follow the path. That is the right
long-term answer here too, and it is *build-independent* - it reads data files, not
code, so nothing in it depends on build 70291 or on any offset.

What it would fix, exactly: it replaces "discover the obstacle by walking into it"
with "know the obstacle before moving". Every wasted leg in the logs above is a
discovery: four pushes into a hillside, a 30 yd diagonal past the goal, a jam.
A mesh turns all of that into a path with waypoints.

What it does **not** fix: click-to-move is still immediate-mode, so a mesh can only
hand the walker a *sequence of points to click*, not a route the client will follow
around a corner by itself. The client walks in a straight line to the clicked point
and stops when something is in the way, so each waypoint must be visible from the
previous one - which is why a mesh wants a **visibility-graph or funnel** step, not
just A* over triangles.

What is still missing before that is buildable: the classic client ships map data
inside its archive files, and this one is a beta build whose data layout has not
been established here yet. The offsets hunt has been about code; the map data is a
separate question, and it is the actual blocker for a mesh.

In the meantime v10 ships the mesh-free approximation, which is the bug algorithm:
walk straight, and when blocked alternate sides at 55 deg until the goal bearing is
clear again, with the closest-approach telemetry to show whether it is working.

## Limits, stated plainly

* **Terrain wins.** A click on a cliff, a wall or water stops the walk early. The
  report shows it as short `moved` and then `BLOCKED: four clicks in a row moved
  under 0.5 yd`.
* **The camera must not be touched** by you during a run, or the model is stale.
  The loop partly adapts, but a large manual camera move mid-run will waste legs.
* Drags are sent as 12 incremental `WM_MOUSEMOVE`s because the client rotates
  from successive deltas; a single jump is likely to be ignored. Not yet verified
  on a real client.
* Releasing a right-drag may itself be read as a click somewhere. The loop measures
  every leg afterwards, so it self-corrects, but it can waste a leg.
* Distance is not modelled at all now, so a long walk takes more legs than an
  ideal controller would need. That is deliberate: the field data says any model
  built on `dy` is fiction.
* Convergence is by measurement, so it is bounded by `--legs` and by patience, not
  by a distance formula.
* The **follow-camera rule** (camera yaw at a click = the heading of the walk that
  just ended) is supported by one run of six probes and by the fact that it makes
  all six agree. It is not proven. If a v8 run shows the click residuals growing
  leg after leg instead of staying flat, this rule is the thing to doubt first, and
  the fix is to re-measure the centre heading before each leg (one short walk per
  leg) rather than carry it.
* The v7 pass-by stop has **never fired in the field**: in the v6 run only one leg
  stopped at all, and it was already slowing (coast 0.07 yd). Whether the stop key
  can cancel a run mid-stride is still unproven; if it cannot, the report says so
  ("the stop key did not cancel the walk after 6 s") and carries on without stops.
* The control loop is validated **in simulation** (front goal 10 legs, goal behind
  the camera 8 legs with 2 drags, key-only client 7 legs) and the v2 build of it
  converged on the live client (45.33 → 1.93 yd). v4's turn-probe sequence has
  not itself been run against a live client yet.
