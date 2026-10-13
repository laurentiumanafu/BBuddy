# =====================================================================
# hunt30.ps1 - read-only live probe #8 for wowb.exe (1.60.1 build 70291)
#
# hunt29 CONFIRMED (live) and WowBHunt29 (Ghidra) closed the loop:
#   * THE UNIT OBJECT IS *(record+0xD8): vtable +0x00 (CGUnitFnTable
#     0x50CDDC0 for NPCs / 0x50127920 for the player), record back-ptr
#     +0x08, GUID lo/hi +0x10/+0x18, reference stat block intact
#     (health 0x1E30 / max 0x1E38 / power 0x1BC8 / flags 0x1E60).
#   * Handle audit is 83/83 on every occupied slot: handle.lo32 == element
#     index, top8 == family, generation XOR == 0, record+0x04 == fam<<24.
#   * Element tags: 0x80000000 x78 + 6 generational tags -> tolerant check.
#   * FnApiRegister: globals[name] (setfield -10002) + registry (rawset
#     -10000) with stack-balance asserts.
#   * NAME PATH: unit = 0x1FC37F0(&PlayerTargetGuid, 0x20, ...) ->
#     0x25129E0(unit, guid, 0x41, 0, 0) -> 0x2512110(x, buf, 0x132, 0) ->
#     MultiByteToWideChar(0xFDE9 via [0x57E3538]). Name comes from a
#     FUNCTION - it is not stored in the object (hunt29: no string reachable
#     one level from record or unit object, for player AND target).
#
# hunt30 verify-only pass (all lookups NEGATIVE except the registry tables):
#   [0] sanity: guid pair, target guid, camera, L
#   [1] pools: every family, tag histogram, audit ratio
#   [2] player: unit object +0x398/+0x39F packed handle (FnEntityGetGuid),
#       name blobs, GUID-pair copies in the arena
#   [3] WATCH: sample the player record + unit + RECORD during 6 seconds
#       (every 0.4 s) -> position/facing/handle/stats/generation; proves the
#       reader can poll and catches the class-A generation churn live
#   [4] TARGET: record, unit object, +0x398 handle, stat block, name via
#       FnUnitName* function path (if the pointer stub is readable we call
#       nothing - we just verify the offsets hold for an NPC too)
#   [5] API registry tables: dump ALL entries around 0x59096B0 / 0x58CF320 /
#       0x590CB20 from the LIVE process + list their function RVAs; then
#       search for the tables' anchors (the name-string pointers already
#       found: 0x5120A70, 0x5071350, 0x512BF08) and report each table's
#       extent -> a complete API surface map
#   [6] movement handler bytes sanity: read 16 bytes at each movement RVA
#       (they are clean code in the module image) - confirms RVAs live
# Run ELEVATED, in-game, standing still, with an NPC TARGETED:
#   powershell -ExecutionPolicy Bypass -File C:\ghidra-scripts\hunt30.ps1 | Tee-Object C:\ghidra-scripts\hunt30-out.txt
# =====================================================================
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class H30 {
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, out IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool CloseHandle(IntPtr h);
}
"@

$proc = Get-Process -Name wowb -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "wowb process not found - start the game and log in first." }
$mod = $null
try { $mod = $proc.Modules | Where-Object { $_.ModuleName -ieq 'wowb.exe' } | Select-Object -First 1 } catch { }
if (-not $mod) { throw "wowb.exe module not found (run this elevated)." }
$base = [uint64]$mod.BaseAddress.ToInt64()
$modSize = [uint64]$mod.ModuleMemorySize
$hProc = [H30]::OpenProcess(0x0410, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) { throw ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Write-Output ("[HDR] pid={0} base=0x{1:X} moduleSize=0x{2:X}" -f $proc.Id, $base, $modSize)

# ---------------------------------------------------------------- helpers
$U64_LO32 = [uint64]4294967295
$U64_MID24 = [uint64]16777215

function Get-Bytes([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H30]::ReadProcessMemory($hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
  if ($got.ToInt64() -ne $n) { return $null }
  return , $buf
}
function Get-Qword([uint64]$addr) { $b = Get-Bytes $addr 8; if ($null -eq $b) { return [uint64]0 }; return [BitConverter]::ToUInt64($b, 0) }
function Get-Dword([uint64]$addr) { $b = Get-Bytes $addr 4; if ($null -eq $b) { return [uint32]0 }; return [BitConverter]::ToUInt32($b, 0) }
function Get-Floats([uint64]$addr, [int]$n) {
  $b = Get-Bytes $addr $n
  if ($null -eq $b) { return $null }
  $res = New-Object System.Collections.ArrayList
  for ($i = 0; $i + 4 -le $n; $i += 4) { [void]$res.Add([BitConverter]::ToSingle($b, $i)) }
  return , $res
}
function Test-Pointer([uint64]$p) { return (($p -gt 0x10000) -and ($p -lt 0x7FFFFFFFFFFF) -and (($p % 8) -eq 0)) }
function Test-ModulePtr([uint64]$p) { return (($p -ge $base) -and ($p -lt ($base + $modSize))) }
function Qb([byte[]]$b, [int]$o) { return [BitConverter]::ToUInt64($b, $o) }
function Db([byte[]]$b, [int]$o) { return [BitConverter]::ToUInt32($b, $o) }
function Read-AsciiAt([uint64]$p, [int]$maxLen) {
  if (-not (Test-Pointer $p)) { return '' }
  $b = Get-Bytes $p $maxLen
  if ($null -eq $b) { return '' }
  $s = ''
  for ($i = 0; $i -lt $b.Length; $i++) {
    if ($b[$i] -ge 32 -and $b[$i] -lt 127) { $s += [char]$b[$i] } else { break }
  }
  return $s
}
function ReadStr([uint64]$a, [int]$n) {
  $b = Get-Bytes $a $n
  if ($null -eq $b) { return '' }
  return (([Text.Encoding]::ASCII.GetString($b)) -split "`0")[0]
}
function To-Print([string]$s) {
  $o = ''
  foreach ($ch in $s.ToCharArray()) { if ([int]$ch -ge 32 -and [int]$ch -lt 127) { $o += $ch } else { $o += '.' } }
  return $o
}
function Fmt-Handle([uint64]$v) {
  $lo  = [uint64]($v -band $U64_LO32)
  $mid = [uint64](($v -shr 32) -band $U64_MID24)
  $fam = [uint64](($v -shr 56) -band 255)
  return ("idx={0} gen=0x{1:X} fam={2}" -f $lo, $mid, $fam)
}
function Show-Obj([uint64]$rec, [string]$tag) {
  if (-not (Test-Pointer $rec)) { Write-Output ("[{0}] no record" -f $tag); return }
  $h  = Get-Qword ($rec + 0x00)
  $D  = Get-Qword ($rec + 0xD8)
  $pos = Get-Floats ($rec + 0x60) 12
  $ff  = Get-Floats ($rec + 0x70) 4
  $face = 0.0; if ($ff) { $face = [double]$ff[0] }
  Write-Output ("[{0}] rec=0x{1:X} handle=0x{2:X16} [{3}] flags=0x{4:X} D=0x{5:X}" -f $tag, $rec, $h, (Fmt-Handle $h), (Get-Dword ($rec + 0x48)), $D)
  if ($pos) { Write-Output ("[{0}] pos=({1:F3},{2:F3},{3:F3}) facing={4:F5}" -f $tag, $pos[0], $pos[1], $pos[2], $face) }
  if (-not (Test-Pointer $D)) { return }
  $vt = Get-Qword $D
  $vtRva = $vt - $base
  $kind = 'unknown'
  if ($vtRva -eq 0x50CDDC0) { $kind = 'CGUnitFnTable (unit/npc)' } 
  elseif ($vtRva -ge 0x50127920 -and $vtRva -lt 0x50127A00) { $kind = 'player-vtable' }
  $p398 = Get-Qword ($D + 0x398)
  $b39F = -1
  $bb = Get-Bytes ($D + 0x39F) 1
  if ($bb) { $b39F = [int]$bb[0] }
  $b3AC = -1
  $bc = Get-Bytes ($D + 0x3AC) 1
  if ($bc) { $b3AC = [int]$bc[0] }
  $guidLo = Get-Qword ($D + 0x10)
  $guidHi = Get-Qword ($D + 0x18)
  $back   = Get-Qword ($D + 0x08)
  Write-Output ("[{0}] unit=0x{1:X} vtable=base+0x{2:X} [{3}] back->0x{4:X} guid={{0x{5:X}, 0x{6:X16}}}" -f $tag, $D, $vtRva, $kind, $back, $guidLo, $guidHi)
  Write-Output ("[{0}] unit+0x398=0x{1:X16} [{2}]  unit+0x39F=0x{3:X2}  unit+0x3AC={4}" -f $tag, $p398, (Fmt-Handle $p398), $b39F, $b3AC)
  Write-Output ("[{0}] STATS hp={1}/{2} power={3}/{4} flags=0x{5:X} (data=0x{6:X})" -f $tag, (Get-Dword ($D+0x1E30)), (Get-Dword ($D+0x1E38)), (Get-Dword ($D+0x1BC8)), (Get-Dword ($D+0x1BD0)), (Get-Dword ($D+0x1E60)), $D)
  # vtable slots (first 16) - these are the CGUnit/CGPlayer virtuals
  $vtb = Get-Bytes $vt 0x80
  if ($vtb) {
    $slots = ''
    for ($i = 0; $i -lt 0x80; $i += 8) {
      $fn = Qb $vtb $i
      if (Test-ModulePtr $fn) { $slots += (' [{0}]=0x{1:X}' -f ($i/8), ($fn - $base)) }
    }
    Write-Output ("[{0}] vtable slots:{1}" -f $tag, $slots)
  }
}

# ---------------------------------------------------------------- [0] sanity
Write-Output "== [0] sanity =="
$pg  = Get-Qword ($base + 0x61C72F0)
$pgh = Get-Qword ($base + 0x61C72F8)
$pName = To-Print (ReadStr ($base + 0x61C7300) 24)
$tg  = Get-Qword ($base + 0x7BDFF38)
$tgh = Get-Qword ($base + 0x7BDFF40)
Write-Output ("[SAN] player_guid {{0x{0:X}, 0x{1:X16}}} name='{2}'" -f $pg, $pgh, $pName)
Write-Output ("[SAN] target_guid {{0x{0:X}, 0x{1:X16}}}" -f $tg, $tgh)
$camX = 0.0; $camY = 0.0; $camZ = 0.0; $camZoom = 0.0
$camRoot = Get-Qword ($base + 0x5FF99F0)
if (Test-Pointer $camRoot) {
  $cam = Get-Qword ($camRoot + 0x490)
  if (Test-Pointer $cam) {
    $cf = Get-Floats ($cam + 0x10) 12
    if ($cf) { $camX = [double]$cf[0]; $camY = [double]$cf[1]; $camZ = [double]$cf[2] }
    $zf = Get-Floats ($cam + 0x274) 4
    if ($zf) { $camZoom = [double]$zf[0] }
    Write-Output ("[SAN] camera origin=({0:F2},{1:F2},{2:F2}) zoom={3:F2}" -f $camX, $camY, $camZ, $camZoom)
  }
}
$L = Get-Qword ($base + 0x7B76AB8)
Write-Output ("[SAN] lua_State L = 0x{0:X}" -f $L)
$Ltt = Get-Dword ($L + 8)
Write-Output ("[SAN] L+8 dword = 0x{0:X} (fingerprint note: NOT a validity test)" -f $Ltt)

# ---------------------------------------------------------------- [1] pools
Write-Output "== [1] pools =="
$poolTable = $base + 0x582D150
$famInfo = New-Object System.Collections.ArrayList
$tagHist = @{}
$totOcc = 0; $totRead = 0; $okLo = 0; $okFam = 0; $okGen = 0
for ($f = 0; $f -lt 32; $f++) {
  $p = Get-Qword ($poolTable + [uint64]($f * 8))
  if (-not (Test-Pointer $p)) { continue }
  $el   = Get-Qword ($p + 0x08)
  $capU = Get-Dword ($p + 0x10)
  $occ = -1
  if ((Test-Pointer $el) -and ($capU -ge 1) -and ($capU -le 4096)) {
    $eb = Get-Bytes $el ([int]($capU * 16))
    if ($eb) {
      $occ = 0
      for ($k = 0; $k -lt [int]$capU; $k++) {
        $o = $k * 16
        $tag = Db $eb $o
        if (([int64]$tag -band 0x80000000) -eq 0) { continue }
        $occ++; $totOcc++
        $tk = ('0x{0:X8}' -f $tag)
        if ($tagHist.ContainsKey($tk)) { $tagHist[$tk] = [int]$tagHist[$tk] + 1 } else { $tagHist[$tk] = 1 }
        $rec = Qb $eb ($o + 8)
        if (-not (Test-Pointer $rec)) { continue }
        $totRead++
        $h = Get-Qword ($rec + 0x00)
        if (($h -band $U64_LO32) -eq [uint64]$k) { $okLo++ }
        if ((($h -shr 56) -band 255) -eq [uint64]$f) { $okFam++ }
        if ((((($h -shr 32) -band $U64_MID24) -bxor ([uint64]($tag -band 0x3FFFFFFF))) -band 0x3FFFFFFF) -eq 0) { $okGen++ }
      }
    }
  }
  Write-Output ("[POOL] fam={0} pool=0x{1:X} elems=0x{2:X} cap={3} occupied={4}" -f $f, $p, $el, $capU, $occ)
  [void]$famInfo.Add(@{ f = $f; p = $p; el = $el; cap = [int]$capU })
}
$histTxt = ($tagHist.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { '{0} x{1}' -f $_.Key, $_.Value }) -join ', '
Write-Output ("[AUDIT] occupied={0} readable={1} lo32==slot:{2} top8==fam:{3} genXor==0:{4}" -f $totOcc, $totRead, $okLo, $okFam, $okGen)
Write-Output ("[AUDIT] tags: {0}" -f $histTxt)

# ---------------------------------------------------------------- [2] player
Write-Output "== [2] player =="
$pRec = [uint64]0; $pFam = -1; $pK = -1
foreach ($fi in $famInfo) {
  $cap = [int]$fi.cap
  if ((-not (Test-Pointer $fi.el)) -or ($cap -lt 1) -or ($cap -gt 4096)) { continue }
  $eb = Get-Bytes $fi.el ([int]($cap * 16))
  if (-not $eb) { continue }
  for ($k = 0; $k -lt $cap; $k++) {
    $o = $k * 16
    if (([int64](Db $eb $o) -band 0x80000000) -eq 0) { continue }
    $rec = Qb $eb ($o + 8)
    if (-not (Test-Pointer $rec)) { continue }
    if (((Get-Qword ($rec + 0x18)) -eq $pg) -and ((Get-Qword ($rec + 0x20)) -eq $pgh)) { $pRec = $rec; $pFam = [int]$fi.f; $pK = $k }
  }
}
if ($pRec -eq 0) { Write-Output "[PLAYER] record not found" } else {
  Show-Obj $pRec "PLAYER"
  # GUID pair copies in the arena (+/-0x4000)
  $pat = New-Object byte[] 16
  [BitConverter]::GetBytes([uint64]$pg).CopyTo($pat, 0)
  [BitConverter]::GetBytes([uint64]$pgh).CopyTo($pat, 8)
  $hits = New-Object System.Collections.ArrayList
  $scanStart = $pRec - 0x4000
  for ($off = 0; $off -lt 0x8000 -and $hits.Count -lt 8; $off += 0x1000) {
    $chunkAddr = $scanStart + [uint64]$off
    $b = Get-Bytes $chunkAddr 0x1000
    if ($null -eq $b) { continue }
    for ($i = 0; $i + 16 -le $b.Length; $i += 8) {
      if ((Qb $b $i) -eq $pg -and (Qb $b ($i+8)) -eq $pgh) { [void]$hits.Add($chunkAddr + [uint64]$i) }
    }
  }
  Write-Output ("[PLAYER] GUID-pair copies within +/-0x4000 of the record: {0}" -f $hits.Count)
  foreach ($h in $hits) { Write-Output ("[PLAYER]   at 0x{0:X} (rec{1}0x{2:X})" -f $h, $(if ($h -ge $pRec) { '+' } else { '-' }), [Math]::Abs([int64]($h - $pRec))) }
}

# ---------------------------------------------------------------- [3] watch
Write-Output "== [3] 6-second watch (poll every 0.4s) =="
if ($pRec -ne 0) {
  for ($t = 0; $t -lt 15; $t++) {
    $h = Get-Qword ($pRec + 0x00)
    $pos = Get-Floats ($pRec + 0x60) 12
    $ff = Get-Floats ($pRec + 0x70) 4
    $facing = 0.0; if ($ff) { $facing = [double]$ff[0] }
    $D = Get-Qword ($pRec + 0xD8)
    $hp = 0; $mp = 0
    if (Test-Pointer $D) { $hp = Get-Dword ($D + 0x1E30); $mp = Get-Dword ($D + 0x1BC8) }
    if ($pos) {
      Write-Output ("[WATCH] t={0:F1}s rec=0x{1:X} handle=0x{2:X16} pos=({3:F3},{4:F3},{5:F3}) facing={6:F5} hp={7} pow={8}" -f ($t*0.4), $pRec, $h, $pos[0], $pos[1], $pos[2], $facing, $hp, $mp)
    }
    Start-Sleep -Milliseconds 400
  }
}

# ---------------------------------------------------------------- [4] target
Write-Output "== [4] target =="
if (($tg -eq 0) -and ($tgh -eq 0)) { Write-Output "[TGT] no target" } else {
  $tRec = [uint64]0
  foreach ($fi in $famInfo) {
    $cap = [int]$fi.cap
    if ((-not (Test-Pointer $fi.el)) -or ($cap -lt 1) -or ($cap -gt 4096)) { continue }
    $eb = Get-Bytes $fi.el ([int]($cap * 16))
    if (-not $eb) { continue }
    for ($k = 0; $k -lt $cap; $k++) {
      $o = $k * 16
      if (([int64](Db $eb $o) -band 0x80000000) -eq 0) { continue }
      $rec = Qb $eb ($o + 8)
      if (-not (Test-Pointer $rec)) { continue }
      if (((Get-Qword ($rec + 0x18)) -eq $tg) -and ((Get-Qword ($rec + 0x20)) -eq $tgh)) { $tRec = $rec }
    }
  }
  if ($tRec -eq 0) { Write-Output "[TGT] no pool record for the target guid" } else { Show-Obj $tRec "TARGET" }
}

# ---------------------------------------------------------------- [5] registry
Write-Output "== [5] FrameScript API registry (live entries) =="
$namePtrs = @{
  'CastSpellByName'  = 0x5120A70
  'MoveForwardStart' = 0x5071350
  'GetActionInfo'    = 0x512BF08
}
foreach ($kn in $namePtrs.Keys) {
  # layout is {char* name; void* fn} with fn stored as ABSOLUTE VA; the name
  # pointer and the fn pointer are interleaved as 16-byte records, so walk
  # records where qword[0] is a module name-ptr and qword[1] a module code-ptr.
  $anchor = $base + [uint64]$namePtrs[$kn]
  # find the record whose first qword == anchor (the entry itself)
  $entry = [uint64]0
  foreach ($delta in @(0x10, 0x8, 0x0)) {
    $a = $anchor + [uint64]$delta
    if ((Get-Qword $a) -eq $anchor) { $entry = $a; break }
  }
  if ($entry -eq 0) {
    foreach ($delta in @(0x8, 0x10)) {
      $a = $anchor - [uint64]$delta
      if ((Get-Qword $a) -eq $anchor) { $entry = $a; break }
    }
  }
  if ($entry -eq 0) { Write-Output ("[REG] {0}: entry for anchor not found in +-16" -f $kn); continue }
  Write-Output ("[REG] {0}: entry @ base+0x{1:X}" -f $kn, ($entry - $base))
  $entries = New-Object System.Collections.ArrayList
  # walk forward and back in 16-byte records
  $i = 0
  while ($i -lt 400) {
    $a = $entry + [uint64]($i * 16)
    $nq = Get-Qword $a
    $fq = Get-Qword ($a + 8)
    if ((-not (Test-ModulePtr $nq)) -or (-not (Test-ModulePtr $fq))) { break }
    $s = Read-AsciiAt $nq 40
    if ($s.Length -lt 3) { break }
    if (($fq - $base) -gt 0x4000000) { break }
    [void]$entries.Add(@{ off = ($i * 16); n = $s; fn = ($fq - $base); neg = $false })
    $i++
  }
  $j = 1
  while ($j -lt 400) {
    $a = $entry - [uint64]($j * 16)
    $nq = Get-Qword $a
    $fq = Get-Qword ($a + 8)
    if ((-not (Test-ModulePtr $nq)) -or (-not (Test-ModulePtr $fq))) { break }
    $s = Read-AsciiAt $nq 40
    if ($s.Length -lt 3) { break }
    if (($fq - $base) -gt 0x4000000) { break }
    [void]$entries.Insert(0, @{ off = -($j * 16); n = $s; fn = ($fq - $base); neg = $true })
    $j++
  }
  Write-Output ("[REG] {0}: table extent {1} entries (fwd={2} bwd={3})" -f $kn, $entries.Count, $i, ($j - 1))
  $n = 0
  foreach ($e in $entries) {
    Write-Output ("[REG]   {0}{1:X4}  {2,-34} -> 0x{3:X}" -f $(if ($e.neg) { '-' } else { '+' }), [Math]::Abs([int64]$e.off), $e.n, $e.fn)
    $n++
    if ($n -ge 80) { Write-Output "[REG]   ... truncated at 80"; break }
  }
}

# ---------------------------------------------------------------- [6] handler bytes
Write-Output "== [6] movement/handler RVA byte check =="
$check = @(0x23C8BE0, 0x23C9000, 0x23C5560, 0x23C76C0, 0x312CB70, 0x2C24040, 0x2CC2DB0, 0x2CDA340, 0x1FC37F0, 0x25129E0, 0x2512110, 0xAE96A0, 0x1D98820)
foreach ($r in $check) {
  $b = Get-Bytes ($base + [uint64]$r) 16
  if ($null -eq $b) { Write-Output ("[CODE] 0x{0:X}: unreadable" -f $r); continue }
  $hex = ($b | ForEach-Object { $_.ToString('X2') }) -join ' '
  Write-Output ("[CODE] 0x{0:X}: {1}" -f $r, $hex)
}

[void][H30]::CloseHandle($hProc)
Write-Output "[DONE]"
