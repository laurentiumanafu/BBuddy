@echo off
rem LINT.cmd - run every static check this repository has, before a build.
rem
rem   Tools\Lint.cmd
rem
rem None of these need a .NET SDK. Each one was written in response to a real build
rem failure, and each one was verified by reintroducing that failure.
setlocal
cd /d "%~dp0.."

echo --- syntax (tree-sitter) ---
if exist Tools\CsParseCheck\node_modules (
  node Tools\CsParseCheck\check.cjs Styx\Logic\Pathing\ClickAim\*.cs Styx\WoWInternals\WowBSigScan.cs
) else (
  echo skipped: Tools\CsParseCheck\node_modules is missing ^(npm i there^)
)

echo --- names in the files under work ---
python Tools\lint_cs_usings.py .
python Tools\lint_cs_fields.py .
python Tools\lint_cs_constants.py
python Tools\lint_cs_overflow_literals.py .
python Tools\lint_call_arity.py . --strict
rem no path: the undeclared check is file-local and is scoped to ClickAim on purpose
python Tools\lint_cs_undeclared.py
python Tools\lint_cs_duplicate_types.py
endlocal
