# =====================================================================
# hunt29.ps1 - read-only live probe #7 for wowb.exe (1.60.1 build 70291)
#
# hunt28 CONFIRMED:
#   * record+0xD8 = unit DATA BLOCK and the reference stat layout survives:
#       data+0x1E30 health, data+0x1E38 maxHealth (live 84/84),
#       data+0x1BC8 power (live 96), data+0x1E60 unit flags (8)
#   * record+0x00 = packed handle {lo32 = element index, mid24 = gen,
#     top8 = pool-table index}, record+0x04 = family << 24
#   * two record flavours: class A 0x60-stride (flags @+0x48 read the
#     0xCECECECE marker, no transform - slots 2..15 live) and class B
#     0x110-stride (real world objects with transform - slots 1, 16+)
#   * the player NAME sits INLINE in the module at 0x61C7300
#
# hunt29 goes after what is still missing:
#   [1] pools + element-tag histogram + FULL handle/generation audit
#       (hunt28's ratio line was killed by a PowerShell bug: 0xFFFFFFFF
#        parses as -1, so masks are explicit [uint64] here)
#   [2] player object: record, owner descriptor, data block, STAT BLOCK
#       printed for eyeball confirmation (health/mana/level)
#   [3] NAME HARVEST: one-level pointer walk over record/data-block/target
#       collecting readable strings -> the unit name must fall out of it
#   [4] class-A records (0x60-stride): what are slots 2..15?
#   [5] current target: guid -> record -> stats -> name harvest
#   [6] FrameScript API REGISTRY: scan the module for the known handler RVAs
#       (CastSpellByName 0x2C24040, MoveForwardStart 0x23C8BE0,
#       RunMacro 0x2CC2DB0) and for the API name strings, then walk the
#       {name, fn} table heuristically -> the ENTIRE API surface
#       (clean impl pointers + names) = Phase-3 keystone
# Run ELEVATED, in-game, standing still, with an NPC TARGETED:
#   powershell -ExecutionPolicy Bypass -File C:\ghidra-scripts\hunt29.ps1 | Tee-Object C:\ghidra-scripts\hunt29-out.txt
# =====================================================================
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class H29 {
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
$hProc = [H29]::OpenProcess(0x0410, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) { throw ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Write-Output ("[HDR] pid={0} base=0x{1:X} moduleSize=0x{2:X}" -f $proc.Id, $base, $modSize)

# ---------------------------------------------------------------- helpers
$U64_LO32 = [uint64]4294967295
$U64_MID24 = [uint64]16777215
$LATIN1 = [Text.Encoding]::GetEncoding(28591)

function Get-Bytes([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H29]::ReadProcessMemory($hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
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
function ReadStr([uint64]$a, [int]$n) {
  $b = Get-Bytes $a $n
  if ($null -eq $b) { return '' }
  return (([Text.Encoding]::ASCII.GetString($b)) -split "`0")[0]
}
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
function Fmt-Rel([uint64]$a, [string]$tag) {
  $d = [int64]($a - $base)
  return ('{0}+0x{1:X}' -f $tag, $d)
}
# readable ASCII strings reached one level through pointers in [addr, addr+len)
function Harvest-PtrStrings([uint64]$addr, [int]$len, [string]$tag, [int]$maxOut) {
  $b = Get-Bytes $addr $len
  if ($null -eq $b) { Write-Output ("[{0}] unreadable" -f $tag); return }
  $out = 0
  for ($i = 0; $i + 8 -le $len; $i += 8) {
    $v = Qb $b $i
    if (-not (Test-Pointer $v)) { continue }
    $s = Read-AsciiAt $v 48
    if ($s.Length -ge 4) {
      Write-Output ("[{0}] +0x{1:X3} -> 0x{2:X} '{3}'" -f $tag, $i, $v, (To-Print $s))
      $out++
      if ($out -ge $maxOut) { Write-Output ("[{0}] ... more suppressed" -f $tag); return }
    }
  }
  if ($out -eq 0) { Write-Output ("[{0}] no string reached from {1} bytes" -f $tag, $len) }
}
# byte-pattern scan over an arbitrary range (chunked, with overlap)
function Scan-RangePattern([byte[]]$pat, [uint64]$lo, [uint64]$hi, [int]$cap) {
  $hits = New-Object System.Collections.ArrayList
  if ($hi -le $lo) { return , $hits }
  $chunk = 0x10000
  $plen = $pat.Length
  $ps = $LATIN1.GetString($pat)
  $over = New-Object byte[] 0
  $overAddr = $lo
  $a = $lo
  while (($a -lt $hi) -and ($hits.Count -lt $cap)) {
    $n = [int][Math]::Min([int64]$chunk, [int64]($hi - $a))
    $b = Get-Bytes $a $n
    if ($null -ne $b) {
      if ($over.Length -gt 0) {
        $comb = New-Object byte[] ($over.Length + $b.Length)
        [Array]::Copy($over, 0, $comb, 0, $over.Length)
        [Array]::Copy($b, 0, $comb, $over.Length, $b.Length)
        $s = $LATIN1.GetString($comb)
        $sbase = $overAddr
      } else {
        $s = $LATIN1.GetString($b)
        $sbase = $a
      }
      $idx = 0
      while ($idx -lt $s.Length) {
        $j = $s.IndexOf($ps, $idx)
        if ($j -lt 0) { break }
        [void]$hits.Add($sbase + [uint64]$j)
        if ($hits.Count -ge $cap) { break }
        $idx = $j + 1
      }
      if (($plen -gt 1) -and ($b.Length -ge ($plen - 1))) {
        $over = New-Object byte[] ($plen - 1)
        [Array]::Copy($b, $b.Length - ($plen - 1), $over, 0, $plen - 1)
        $overAddr = $a + [uint64]($b.Length - ($plen - 1))
      }
    }
    $a += [uint64]$n
  }
  return , $hits
}
function Show-Head([uint64]$addr, [int]$len, [string]$tag) {
  $b = Get-Bytes $addr $len
  if ($null -eq $b) { Write-Output ("[{0}] unreadable" -f $tag); return }
  for ($o = 0; $o + 0x20 -le $len; $o += 0x20) {
    $q0 = Qb $b $o; $q1 = Qb $b ($o+8); $q2 = Qb $b ($o+16); $q3 = Qb $b ($o+24)
    $note = ''
    foreach ($q in @($q0, $q1, $q2, $q3)) {
      if (Test-ModulePtr $q) { $note += (' [MOD+0x{0:X}]' -f ($q - $base)) }
      elseif (Test-Pointer $q) {
        $s = Read-AsciiAt $q 16
        if ($s.Length -ge 4) { $note += (' ["{0}"]' -f (To-Print $s)) }
      }
    }
    Write-Output ("[{0}] +0x{1:X3}: {2:X16} {3:X16} {4:X16} {5:X16}{6}" -f $tag, $o, $q0, $q1, $q2, $q3, $note)
  }
}
# heuristic {name, fn} table walk around a confirmed entry
function Walk-ApiTable([uint64]$entry, [string]$tag) {
  Write-Output ("[{0}] walking around 0x{1:X} for name/fn pairs" -f $tag, $entry)
  foreach ($stride in @(16, 24, 32)) {
    $fwd = 0; $bwd = 0
    $list = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt 400; $i++) {
      $a2 = $entry + [uint64]($i * $stride)
      $nameQ = Get-Qword $a2
      $fnQ = Get-Qword ($a2 + 8)
      if ((-not (Test-ModulePtr $nameQ)) -or (-not (Test-ModulePtr $fnQ))) { break }
      $s = Read-AsciiAt $nameQ 32
      if ($s.Length -lt 3) { break }
      if (($fnQ - $base) -gt 0x4000000) { break }
      [void]$list.Add(('+{0:X3}  {1,-30} -> 0x{2:X}' -f ($i * $stride), $s, ($fnQ - $base)))
      $fwd++
    }
    for ($i = 1; $i -lt 400; $i++) {
      $a2 = $entry - [uint64]($i * $stride)
      $nameQ = Get-Qword $a2
      $fnQ = Get-Qword ($a2 + 8)
      if ((-not (Test-ModulePtr $nameQ)) -or (-not (Test-ModulePtr $fnQ))) { break }
      $s = Read-AsciiAt $nameQ 32
      if ($s.Length -lt 3) { break }
      if (($fnQ - $base) -gt 0x4000000) { break }
      [void]$list.Insert(0, ('-{0:X3}  {1,-30} -> 0x{2:X}' -f ($i * $stride), $s, ($fnQ - $base)))
      $bwd++
    }
    if (($fwd + $bwd) -ge 3) {
      Write-Output ("[{0}] STRIDE {1}: {2} entries (fwd={3} bwd={4}) - showing up to 60" -f $tag, $stride, ($fwd + $bwd), $fwd, $bwd)
      $shown = 0
      foreach ($l in $list) { Write-Output ("[{0}]   {1}" -f $tag, $l); $shown++; if ($shown -ge 60) { Write-Output ("[{0}]   ..." -f $tag); break } }
      return
    }
  }
  Write-Output ("[{0}] no clean name/fn table around that address (stride 16/24/32)" -f $tag)
}

# ---------------------------------------------------------------- [0] sanity
Write-Output "== [0] sanity =="
$pg  = Get-Qword ($base + 0x61C72F0)
$pgh = Get-Qword ($base + 0x61C72F8)
$pName = To-Print (ReadStr ($base + 0x61C7300) 24)
Write-Output ("[SAN] player_guid lo=0x{0:X} hi=0x{1:X}  name='{2}'" -f $pg, $pgh, $pName)
$tg  = Get-Qword ($base + 0x7BDFF38)
$tgh = Get-Qword ($base + 0x7BDFF40)
$tgLast = Get-Qword ($base + 0x7BDFF48)
Write-Output ("[SAN] target_guid lo=0x{0:X} hi=0x{1:X}  lastTarget_lo=0x{2:X}" -f $tg, $tgh, $tgLast)
$camX = 0.0; $camY = 0.0; $camZ = 0.0; $camZoom = 0.0
$camRoot = Get-Qword ($base + 0x5FF99F0)
if (Test-Pointer $camRoot) {
  $cam = Get-Qword ($camRoot + 0x490)
  if (Test-Pointer $cam) {
    $cf = Get-Floats ($cam + 0x10) 12
    if ($cf) { $camX = [double]$cf[0]; $camY = [double]$cf[1]; $camZ = [double]$cf[2] }
    $zf = Get-Floats ($cam + 0x274) 4
    if ($zf) { $camZoom = [double]$zf[0] }
    Write-Output ("[SAN] camera 0x{0:X} origin=({1:F2},{2:F2},{3:F2}) zoom={4:F2}" -f $cam, $camX, $camY, $camZ, $camZoom)
  } else { Write-Output "[SAN] camera object invalid" }
} else { Write-Output "[SAN] camera root invalid" }
$L = Get-Qword ($base + 0x7B76AB8)
Write-Output ("[SAN] lua_State L = *(base+0x7B76AB8) = 0x{0:X}" -f $L)

# ---------------------------------------------------------------- [1] pools
Write-Output "== [1] pools + handle audit =="
$poolTable = $base + 0x582D150
$famInfo = New-Object System.Collections.ArrayList
$tagHist = @{}
$totOcc = 0; $totRead = 0; $okLo = 0; $okFam = 0; $okGen = 0; $okRecFam = 0
for ($f = 0; $f -lt 32; $f++) {
  $p = Get-Qword ($poolTable + [uint64]($f * 8))
  if (-not (Test-Pointer $p)) { continue }
  $flag = Get-Dword ($p + 0x00)
  $el   = Get-Qword ($p + 0x08)
  $capU = Get-Dword ($p + 0x10)
  $maxV = Get-Dword ($p + 0x18)
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
        $famD = Get-Dword ($rec + 0x04)
        if (($h -band $U64_LO32) -eq [uint64]$k) { $okLo++ }
        if ((($h -shr 56) -band 255) -eq [uint64]$f) { $okFam++ }
        $gen = ((($h -shr 32) -band $U64_MID24) -bxor ([uint64]($tag -band 0x3FFFFFFF)))
        if (($gen -band 0x3FFFFFFF) -eq 0) { $okGen++ }
        if (($famD -shr 24) -eq [uint64]$f) { $okRecFam++ }
      }
    }
  }
  Write-Output ("[POOL] fam={0} pool=0x{1:X} flag={2} elems=0x{3:X} cap={4} max={5} occupied={6}" -f $f, $p, $flag, $el, $capU, $maxV, $occ)
  [void]$famInfo.Add(@{ f = $f; p = $p; el = $el; cap = [int]$capU })
}
if ($famInfo.Count -eq 0) { Write-Output "[POOL] table unreadable" }
$histTxt = ($tagHist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 | ForEach-Object { '{0} x{1}' -f $_.Key, $_.Value }) -join ', '
Write-Output ("[AUDIT] occupied={0} readable={1} handle.lo32==slot:{2} handle.top8==fam:{3} generationXor==0:{4} rec.dword4==fam<<24:{5}" -f $totOcc, $totRead, $okLo, $okFam, $okGen, $okRecFam)
Write-Output ("[AUDIT] element tags: {0}" -f $histTxt)

# ---------------------------------------------------------------- [2] player
Write-Output "== [2] player object =="
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
function Show-ObjectStats([uint64]$rec, [string]$tag) {
  if (-not (Test-Pointer $rec)) { Write-Output ("[{0}] no record" -f $tag); return }
  $h  = Get-Qword ($rec + 0x00)
  $fl = Get-Dword ($rec + 0x48)
  $E  = Get-Qword ($rec + 0x08)
  $D  = Get-Qword ($rec + 0xD8)
  $pos = Get-Floats ($rec + 0x60) 12
  $ff  = Get-Floats ($rec + 0x70) 4
  $face = 0.0; if ($ff) { $face = [double]$ff[0] }
  Write-Output ("[{0}] rec=0x{1:X} handle=0x{2:X16} [{3}] flags=0x{4:X} E=0x{5:X} D=0x{6:X}" -f $tag, $rec, $h, (Fmt-Handle $h), $fl, $E, $D)
  if ($pos) { Write-Output ("[{0}] pos=({1:F2},{2:F2},{3:F2}) facing={4:F4}" -f $tag, $pos[0], $pos[1], $pos[2], $face) }
  if (Test-Pointer $E) {
    Write-Output ("[{0}] E+0x00=0x{1:X} E+0x08=0x{2:X} E+0x10=0x{3:X} E+0x18=0x{4:X}" -f $tag, (Get-Qword $E), (Get-Qword ($E + 8)), (Get-Dword ($E + 0x10)), (Get-Qword ($E + 0x18)))
  }
  if (-not (Test-Pointer $D)) { Write-Output ("[{0}] no data block" -f $tag); return }
  $hp = Get-Dword ($D + 0x1E30); $hpm = Get-Dword ($D + 0x1E38)
  $pw = Get-Dword ($D + 0x1BC8); $pwm = Get-Dword ($D + 0x1BD0)
  $uf = Get-Dword ($D + 0x1E60)
  Write-Output ("[{0}] STATS data=0x{1:X}: health={2}/{3} power={4}/{5} unitFlags=0x{6:X}" -f $tag, $D, $hp, $hpm, $pw, $pwm, $uf)
  $lohi = ''
  foreach ($off in @(0x1BC0, 0x1BC4, 0x1BC8, 0x1BCC, 0x1BD0, 0x1BD4, 0x1E28, 0x1E2C, 0x1E30, 0x1E34, 0x1E38, 0x1E3C, 0x1E60, 0x1E64, 0x1E68, 0x1E6C)) {
    $lohi += (' +0x{0:X}={1}' -f $off, (Get-Dword ($D + $off)))
  }
  Write-Output ("[{0}] dwords:{1}" -f $tag, $lohi)
  $lv = ''
  for ($off = 0x1D80; $off -lt 0x1E80; $off += 4) {
    $v = Get-Dword ($D + $off)
    if (($v -ge 1) -and ($v -le 80)) { $lv += (' +0x{0:X}={1}' -f $off, $v) }
  }
  if ($lv -ne '') { Write-Output ("[{0}] small values 1..80 (level candidates):{1}" -f $tag, $lv) }
}
if ($pRec -eq 0) { Write-Output "[PLAYER] record NOT found by guid pair" } else { Show-ObjectStats $pRec "PLAYER" }

# ---------------------------------------------------------------- [3] name harvest
Write-Output "== [3] name harvest =="
if ($pRec -ne 0) {
  Harvest-PtrStrings $pRec 0x110 "NM-REC" 12
  $pD = Get-Qword ($pRec + 0xD8)
  if (Test-Pointer $pD) {
    Harvest-PtrStrings $pD 0x800 "NM-DAT" 12
    Harvest-PtrStrings ($pD + 0x1800) 0x800 "NM-DAT2" 16
    $n238 = Get-Qword ($pD + 0x238)
    Write-Output ("[NM-DAT] data+0x238 = 0x{0:X} str='{1}'" -f $n238, (To-Print (Read-AsciiAt $n238 40)))
  }
  $patGuid = New-Object byte[] 16
  [BitConverter]::GetBytes([uint64]$pg).CopyTo($patGuid, 0)
  [BitConverter]::GetBytes([uint64]$pgh).CopyTo($patGuid, 8)
  $gLo = $pRec - 0x100000
  $hits = Scan-RangePattern $patGuid $gLo ([uint64]($pRec + 0x100000)) 6
  if ($hits.Count -eq 0) { Write-Output "[NM-GUID] no extra copies of the player GUID pair within +/-0x100000 of the record" }
  foreach ($h in $hits) { Write-Output ("[NM-GUID] pair @ 0x{0:X}" -f $h) }
}
if ($pName.Length -ge 4) {
  $patName = [Text.Encoding]::ASCII.GetBytes($pName)
  $hits = Scan-RangePattern $patName $base ([uint64]($base + $modSize)) 8
  Write-Output ("[NM-BLOB] '{0}' occurs {1}x in the module image" -f $pName, $hits.Count)
  foreach ($h in $hits) { Write-Output ("[NM-BLOB]   0x{0:X} (base+0x{1:X})" -f $h, ($h - $base)) }
}

# ---------------------------------------------------------------- [4] class A
Write-Output "== [4] class-A records (0x60-stride, poison flags) =="
if ($pRec -ne 0) {
  $el = [uint64]0; $cap = 0
  foreach ($fi in $famInfo) { if ([int]$fi.f -eq $pFam) { $el = [uint64]$fi.el; $cap = [int]$fi.cap } }
  $eb = [byte[]]$null
  if ((Test-Pointer $el) -and ($cap -ge 1) -and ($cap -le 4096)) { $eb = Get-Bytes $el ([int]($cap * 16)) }
  if ($eb) {
    $shown = 0; $prev = [uint64]0; $firstA = [uint64]0
    for ($k = 0; $k -lt $cap; $k++) {
      if ($k -eq $pK) { continue }
      $o = $k * 16
      if (([int64](Db $eb $o) -band 0x80000000) -eq 0) { continue }
      $rec = Qb $eb ($o + 8)
      if (-not (Test-Pointer $rec)) { continue }
      if ((Get-Dword ($rec + 0x48)) -ne 0xCECECECE) { continue }
      if ($firstA -eq 0) { $firstA = $rec }
      if ($shown -lt 16) {
        $delta = '-'
        if ($prev -ne 0) { $delta = ('0x{0:X}' -f ($rec - $prev)) }
        $prev = $rec
        Write-Output ("[CLSA] slot={0,3} rec=0x{1:X} delta={2} guidLo=0x{3:X} guidHi=0x{4:X16} q40={5:X16} q50={6:X16} q58={7:X16}" -f $k, $rec, $delta, (Get-Qword ($rec + 0x18)), (Get-Qword ($rec + 0x20)), (Get-Qword ($rec + 0x40)), (Get-Qword ($rec + 0x50)), (Get-Qword ($rec + 0x58)))
      }
      $shown++
    }
    Write-Output ("[CLSA] poison-flag records: {0}" -f $shown)
    if ($firstA -ne 0) { Show-Head $firstA 0x60 "CLSA-HEAD" }
  }
}

# ---------------------------------------------------------------- [5] target
Write-Output "== [5] current target =="
if (($tg -eq 0) -and ($tgh -eq 0)) {
  Write-Output "[TGT] no target right now (target the NPC next to you and rerun)"
} else {
  $tRec = [uint64]0; $tFam = -1
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
      if (((Get-Qword ($rec + 0x18)) -eq $tg) -and ((Get-Qword ($rec + 0x20)) -eq $tgh)) { $tRec = $rec; $tFam = [int]$fi.f }
    }
  }
  if ($tRec -eq 0) { Write-Output ("[TGT] guid {{0x{0:X}, 0x{1:X}}} has no pool record" -f $tg, $tgh) }
  else {
    Show-ObjectStats $tRec "TARGET"
    Harvest-PtrStrings $tRec 0x110 "NM-TGT-REC" 8
    $tD = Get-Qword ($tRec + 0xD8)
    if (Test-Pointer $tD) {
      Harvest-PtrStrings $tD 0x800 "NM-TGT-DAT" 20
      Harvest-PtrStrings ($tD + 0x1800) 0x800 "NM-TGT-DAT2" 20
      $n = Get-Qword ($tD + 0x238)
      Write-Output ("[NM-TGT] data+0x238 = 0x{0:X} str='{1}'" -f $n, (To-Print (Read-AsciiAt $n 40)))
      Show-Head $tD 0x60 "TGT-DAT"
    }
  }
}

# ---------------------------------------------------------------- [6] API registry
Write-Output "== [6] FrameScript API registry hunt (module scan) =="
$scanLo = $base + 0x4000000
$scanHi = [uint64][Math]::Min([int64]($base + $modSize), [int64]($base + 0x8000000))
$known = @(
  @{ n = 'CastSpellByName';  r = 0x2C24040 },
  @{ n = 'MoveForwardStart'; r = 0x23C8BE0 },
  @{ n = 'RunMacro';         r = 0x2CC2DB0 }
)
foreach ($k in $known) {
  $pat = [BitConverter]::GetBytes([uint64]($base + [uint64]$k.r))
  $hits = Scan-RangePattern $pat $scanLo $scanHi 4
  Write-Output ("[API] impl 0x{0:X} ({1}) stored at {2} location(s)" -f $k.r, $k.n, $hits.Count)
  foreach ($h in $hits) {
    Write-Output ("[API]   store @ base+0x{0:X}" -f ($h - $base))
    Show-Head ($h - 0x20) 0x60 ("API-{0}" -f $k.n)
  }
}
foreach ($nm in @('CastSpellByName', 'RunScript', 'MoveForwardStart', 'GetActionInfo')) {
  $pat = [Text.Encoding]::ASCII.GetBytes($nm)
  $hits = Scan-RangePattern $pat $scanLo $scanHi 3
  Write-Output ("[API] string '{0}' found {1}x" -f $nm, $hits.Count)
  foreach ($h in $hits) { Write-Output ("[API]   str @ base+0x{0:X}" -f ($h - $base)) }
  if ($hits.Count -gt 0) {
    $spat = [BitConverter]::GetBytes([uint64]$hits[0])
    $sh = Scan-RangePattern $spat $scanLo $scanHi 4
    Write-Output ("[API]   pointers to that string: {0}" -f $sh.Count)
    foreach ($s in $sh) {
      Write-Output ("[API]     ptr @ base+0x{0:X}" -f ($s - $base))
      Show-Head ($s - 0x20) 0x60 ("APIPTR-{0}" -f $nm)
      Walk-ApiTable $s ("APITBL-{0}" -f $nm)
    }
  }
}

[void][H29]::CloseHandle($hProc)
Write-Output "[DONE]"
