# C# syntax check without an SDK

No `dotnet` or `mono` on the Linux side of this project, so a C# edit could not be
checked before it reached the Windows machine that builds it. `check.cjs` parses the
sources with tree-sitter's C# grammar and reports syntax errors only.

```
cd Tools/CsParseCheck
npm i web-tree-sitter@0.22.6 tree-sitter-wasms
node check.cjs $(find ../.. -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*')
```

It catches unbalanced braces, half-rewritten statements and similar; it does not
type-check. `Tools/lint_cs_types.py` covers the types it can see textually, and the
real acceptance is still the build on the client machine.

One known false positive: `from` as an argument name in
`Bots/DungeonBuddy/Avoidance/Helpers.cs` - the grammar reads the contextual keyword
as a query expression.

## Argument counts

`Tools/lint_call_arity.py` covers the other half: it indexes every method
declaration in the tree (smallest and largest legal argument count per overload) and
checks every call site against those. It is what catches

    error CS7036: There is no argument given that corresponds to the required
    parameter 'nearestPoint' of 'Navigator.FindNearestPolyRef(uint, Vector3, float,
    float, out PolygonReference, out Vector3)'

before the Windows build does. Failures are advisory by default; add `--strict` to
make it exit non-zero:

    python3 Tools/lint_call_arity.py . --strict
