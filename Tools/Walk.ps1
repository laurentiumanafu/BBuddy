<#
    Walk.ps1 - one command to make the character walk somewhere and watch it happen.

    The pilot reads Settings\clickaim-walk.txt, deletes it, and walks. Reading the
    log by hand afterwards is the slow part, so this writes the request, then tails
    the newest log and prints only the lines that matter: the mesh path, the polygon
    report, the blackspot marks, the re-plans and the mover's own legs.

    Usage (from anywhere):
        Tools\Walk.cmd calibrate         measure the click model (do this first)
        Tools\Walk.cmd route:100,200     plan only, print the route, do not walk
        Tools\Walk.cmd 20                20 yd straight ahead of the facing
        Tools\Walk.cmd 100               100 yd, so the mesh has something to route
        Tools\Walk.cmd 1977.5,232         walk to a world position
        Tools\Walk.cmd 1977.5,232,3       ... with a 3 yd tolerance
        Tools\Walk.cmd 20 -Seconds 120    watch for two minutes instead of 60
        Tools\Walk.cmd 20 -Root "F:\World of Warcraft\patch33\BBuddy-clickaim\bin\x64\Release\net10.0-windows7.0"

    The root defaults to this script's parent folder, which is where the build
    output sits when the Tools folder is copied beside the exe.
#>
param(
    [string]$Goal = "",
    [string]$Root = "",
    [int]$Seconds = 0
)

# Walk.cmd passes these in the environment, because PowerShell splits an argument
# on commas: "route:-495.3,-4221.1" arrives as two arguments and the second is read
# as a parameter name. Bare parameters still work when the script is called directly.
if (-not $Goal)    { $Goal = $env:CLICKAIM_GOAL }
if (-not $Root)    { $Root = $env:CLICKAIM_ROOT }
if ($Seconds -le 0 -and $env:CLICKAIM_SECONDS) { $Seconds = [int]$env:CLICKAIM_SECONDS }
if ($Seconds -le 0) { $Seconds = 60 }
if (-not $Goal) {
    Write-Host 'usage: Walk.ps1 -Goal <yards|x,y|route:x,y|calibrate> [-Seconds N] [-Root "exe folder"]'
    exit 2
}

$ErrorActionPreference = "Stop"

function Find-BotRoot {
    # The settings file and the log live beside CopilotBuddy.exe, which is not
    # necessarily beside this script: it is usually a build output folder.
    param([string]$Start)

    $candidates = @()
    if ($Start) { $candidates += $Start }
    $candidates += @(
        (Join-Path $Start "bin\x64\Release\net10.0-windows7.0"),
        (Join-Path $Start "bin\x64\Release\net10.0-windows"),
        (Join-Path $Start "bin\x64\Release"),
        (Join-Path $Start "bin\Release"),
        $Start
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "CopilotBuddy.exe"))) { return (Resolve-Path $c).Path }
        if ($c -and (Test-Path (Join-Path $c "Settings")))       { return (Resolve-Path $c).Path }
    }
    return $Start
}

if (-not $Root) { $Root = Find-BotRoot (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
if (-not (Test-Path $Root)) { throw "no such folder: $Root" }
if (-not (Test-Path (Join-Path $Root "CopilotBuddy.exe"))) {
    Write-Host "note: no CopilotBuddy.exe in $Root - pass -Root with the folder that has it"
}

$settings = Join-Path $Root "Settings"
$request = Join-Path $settings "clickaim-walk.txt"
if (-not (Test-Path $settings)) { New-Item -ItemType Directory -Path $settings | Out-Null }

Write-Host "goal '$Goal'"
Set-Content -Path $request -Value $Goal -NoNewline -Encoding ASCII
Write-Host "wrote it to $request"

$logDir = Join-Path $Root "Logs"
$log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) { throw "no log in $logDir - is the bot running?" }
Write-Host "tailing $($log.Name) for $Seconds s"
Write-Host ("-" * 78)

$filter = 'ClickAim|Route|Blackspot|MeshProbe|MeshQuery|Calibrate|NavGrid self-test'
$position = $log.Length
$deadline = (Get-Date).AddSeconds($Seconds)
$seen = 0

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    if (-not (Test-Path $log.FullName)) { continue }

    $stream = [System.IO.File]::Open($log.FullName, "Open", "Read", "ReadWrite")
    try {
        if ($stream.Length -lt $position) { $position = 0 }   # log was rotated
        $stream.Seek($position, "Begin") | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        $chunk = $reader.ReadToEnd()
        $position = $stream.Position
        $reader.Dispose()
    }
    finally { $stream.Dispose() }

    if (-not $chunk) { continue }

    foreach ($line in ($chunk -split "`r?`n")) {
        if ($line -match $filter) {
            Write-Host $line
            $seen++
            if ($line -match "arrived|BLOCKED|gave up|no progress|out of re-plans") { $deadline = (Get-Date) }
        }
    }
}

Write-Host ("-" * 78)
if ($seen -eq 0) {
    Write-Host "no ClickAim/Route lines appeared. Is the bot attached with --clickaim-walk,"
    Write-Host "and is this the folder that holds CopilotBuddy.exe?"
}
else { Write-Host "$seen line(s) matched." }
