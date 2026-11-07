# Grind approach tests

Run `python -m unittest discover -s Tools/WowBGrindApproachTests -p 'test*.py'` for source integration checks. Run `python Tools/WowBGrindApproachTests/run.py` with a .NET 10 SDK to compile the production TAB-miss branch and approach predicate against synthetic candidate and movement stubs. These checks do not model the game client, TAB reach, moving monsters, physical obstacles, or a real mesh.

With faction filtering, the first TAB attempts begin at 40 yards. If they miss farther than 26 yards, a fresh candidate can be approached on the validated mesh to 26 yards and TAB retried on the next search. Another miss at or inside 26 yards defers that candidate; it does not force a walk into melee. Selected targets still follow their configured rotation, spell range and resource rules.
