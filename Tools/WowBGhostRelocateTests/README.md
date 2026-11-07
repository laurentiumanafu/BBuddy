# Ghost route relocation fixtures

`python Tools/WowBGhostRelocateTests/run.py` requires a .NET 10 SDK. It compiles the production corpse-route relocation loop with synthetic movement, state and relocation-picker stubs. It checks the retry budget, interruptions, route deadline and arrival handling; it does **not** exercise real movement, the live client, or actual navigation mesh queries. `Tools/MeshRelocateTests/run.py` separately exercises the mesh-picker and Gather relocation logic against synthetic navigation.

The real corpse leg still uses `WalkGhostTo`, whose route-session interrupt, strict mesh requirement, flag restoration and stop checks are asserted by the test script. If a failed leg cannot find a validated alternate, recovery still stops rather than making a blind walk. An mmap-valid destination may nonetheless be physically blocked by an object absent from the mesh.
