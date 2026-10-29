# =====================================================================
# hunt31.ps1 - read-only live probe #9 for wowb.exe (1.60.1 build 70291)
#
# WHAT hunt30 PROVED / BROKE:
#   + player unit vtable = base+0x50A7920 (my 0x50127920 constant was a
#     HEX TYPO - corrected in the repo), NPC = CGUnitFnTable 0x50CDDC0.
#   + 15/15 WATCH samples identical -> the record is a stable poll target;
#     the poll path (pos/facing/handle/hp/power) is milestone-1 DONE.
#   + only ONE copy of the player GUID pair within +/-0x4000 (rec+0x18).
#   - unit+0x398 = 0 and +0x39F = 0xFF for BOTH player and NPC -> the
#     stat/unit object is NOT the object FnEntityGetGuid reads +0x398 from.
#     So "the entity" is a THIRD object shape. hunt27 saw rec+0x08 -> E with
#     E+0x398 = 0xFFFFFFFFFFFFFFFF (an INVALID handle sentinel) and
#     E+0x39F = 0xFF - i.e. E HAS the field, it is just currently invalid.
#     hunt31 tables E for every record and hunts for a VALID handle.
#   - [REG] anchors were wrong: I searched around the STRING address
#     (0x5120A70) instead of the ENTRY address (0x59096B0). Fixed here.
#   - many handler RVAs are UNREADABLE at runtime (0x23C8BE0/0x23C9000/
#     0x23C76C0/0x2C24040/0x2CC2DB0/0x2CDA340) while others read fine
#     (0x23C5560, 0x312CB70, 0x1FC37F0, 0x25129E0, 0x2512110, 0xAE96A0,
#     0x1D98820) -> packed binary with guard/decrypt-on-demand pages.
#     hunt31 maps the protection with VirtualQueryEx and tests whether a
#     temporary VirtualProtectEx lets us read them.
#
# Sections:
#   [0] sanity
#   [1] VirtualQueryEx map for every interesting RVA + VirtualProtectEx retry
#   [2] per-record table: record, unit vtable, unit+0x398/+0x39F,
#       E=*(rec+0x08) and E+0x10/+0x18/+0x398/+0x39F -> who owns a handle?
#   [3] deep dump: record / unit / E heads + handle-shaped qword scan in
#       each object (first 0x4000 bytes)
#   [4] window scan +/-0x200000 around the player record: pointers to the
#       record, to the unit object, and copies of the packed handle
#       -> that is where the "entity table" lives
#   [5] API registry v2 (FIXED anchors): dump the full extent of the three
#       {name, fn} tables, both directions, up to 200 entries each
#   [6] stats/level: dense dword dump 0x1E00..0x1E80 for player + target
# Run ELEVATED, in-game, standing still, with an NPC TARGETED:
#   powershell -ExecutionPolicy Bypass -File C:\ghidra-scripts\hunt31.ps1 | Tee-Object C:\ghidra-scripts\hunt31-out.txt
# =====================================================================
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct MBI31 {
  public ulong BaseAddress;
  public ulong AllocationBase;
  public uint AllocationProtect;
  public uint __a1;
  public ulong RegionSize;
  public uint State;
  public uint Protect;
  public uint Type;
  public uint __a2;
}
public static class H31 {
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, out IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern ulong VirtualQueryEx(IntPtr h, IntPtr addr, out MBI31 mbi, ulong len);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool VirtualProtectExRaw(IntPtr h, IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool FlushInstructionCacheRaw(IntPtr h, IntPtr addr, UIntPtr size);
  public static bool ProtectEx(IntPtr h, IntPtr addr, int size, uint newProtect, out uint oldProtect) {
    return VirtualProtectExRaw(h, addr, (UIntPtr)(ulong)size, newProtect, out oldProtect);
  }
  public static bool FlushIcache(IntPtr h, IntPtr addr, int size) {
    return FlushInstructionCacheRaw(h, addr, (UIntPtr)(ulong)size);
  }
}
"@

$proc = Get-Process -Name wowb -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "wowb process not found - start the game and log in first." }
$mod = $null
try { $mod = $proc.Modules | Where-Object { $_.ModuleName -ieq 'wowb.exe' } | Select-Object -First 1 } catch { }
if (-not $mod) { throw "wowb.exe module not found (run this elevated)." }
$base = [uint64]$mod.BaseAddress.ToInt64()
$modSize = [uint64]$mod.ModuleMemorySize
$hProc = [H31]::OpenProcess(0x0410, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) { throw ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Write-Output ("[HDR] pid={0} base=0x{1:X} moduleSize=0x{2:X}" -f $proc.Id, $base, $modSize)

# ---------------------------------------------------------------- helpers
$U64_LO32 = [uint64]4294967295
$U64_MID24 = [uint64]16777215

function Get-Bytes([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H31]::ReadProcessMemory($hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
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
function RvaOf([uint64]$p) { return ([int64]$p - [int64]$base) }
function Fmt-Handle([uint64]$v) {
  $lo  = [uint64]($v -band $U64_LO32)
  $mid = [uint64](($v -shr 32) -band $U64_MID24)
  $fam = [uint64](($v -shr 56) -band 255)
  return ("idx={0} gen=0x{1:X} fam={2}" -f $lo, $mid, $fam)
}
function Find-ObjHandle([uint64]$obj, [int]$len, [int]$slot, [int]$fam, [string]$tag) {
  # print every qword whose low32 == slot and top8 == fam (a packed handle)
  $b = Get-Bytes $obj $len
  if ($null -eq $b) { Write-Output ("[{0}] unreadable" -f $tag); return }
  $found = 0
  for ($i = 0; $i + 8 -le $len -and $found -lt 6; $i += 4) {
    $v = Qb $b $i
    if ((($v -band $U64_LO32) -eq [uint64]$slot) -and ((($v -shr 56) -band 255) -eq [uint64]$fam)) {
      Write-Output ("[{0}] +0x{1:X3}: handle-shaped 0x{2:X16} [{3}]" -f $tag, $i, $v, (Fmt-Handle $v))
      $found++
    }
  }
  if ($found -eq 0) { Write-Output ("[{0}] no packed handle for slot={1} fam={2} in {3} bytes" -f $tag, $slot, $fam, $len) }
}
function Show-Head([uint64]$addr, [int]$len, [string]$tag) {
  $b = Get-Bytes $addr $len
  if ($null -eq $b) { Write-Output ("[{0}] unreadable" -f $tag); return }
  for ($o = 0; $o + 0x20 -le $len; $o += 0x20) {
    $q0 = Qb $b $o; $q1 = Qb $b ($o+8); $q2 = Qb $b ($o+16); $q3 = Qb $b ($o+24)
    $note = ''
    foreach ($q in @($q0, $q1, $q2, $q3)) {
      if (Test-ModulePtr $q) { $note += (' [MOD+0x{0:X}]' -f (RvaOf $q)) }
      elseif (Test-Pointer $q) {
        $s = Read-AsciiAt $q 16
        if ($s.Length -ge 4) { $note += (' ["{0}"]' -f (To-Print $s)) }
      }
    }
    Write-Output ("[{0}] +0x{1:X3}: {2:X16} {3:X16} {4:X16} {5:X16}{6}" -f $tag, $o, $q0, $q1, $q2, $q3, $note)
  }
}

# ---------------------------------------------------------------- [0] sanity
Write-Output "== [0] sanity =="
$pg  = Get-Qword ($base + 0x61C72F0)
$pgh = Get-Qword ($base + 0x61C72F8)
$pName = To-Print (ReadStr ($base + 0x61C7300) 24)
$tg  = Get-Qword ($base + 0x7BDFF38)
$tgh = Get-Qword ($base + 0x7BDFF40)
Write-Output ("[SAN] player_guid lo=0x{0:X} hi=0x{1:X16} name='{2}'" -f $pg, $pgh, $pName)
Write-Output ("[SAN] target_guid lo=0x{0:X} hi=0x{1:X16}" -f $tg, $tgh)
$L = Get-Qword ($base + 0x7B76AB8)
Write-Output ("[SAN] lua_State L = 0x{0:X}" -f $L)

# ---------------------------------------------------------------- [1] page map
Write-Output "== [1] memory protection map (VirtualQueryEx) =="
$rv = @(0x23C8BE0, 0x23C9000, 0x23C76C0, 0x23C5560, 0x2C24040, 0x2CC2DB0, 0x2CDA340, 0x312CB70,
        0x1FC37F0, 0x25129E0, 0x2512110, 0xAE96A0, 0x1D98820, 0x27FBD0, 0x2BBB7F0, 0x471A960)
function Probe-Rva([uint64]$rva) {
  $addr = $base + $rva
  $mbi = [Activator]::CreateInstance([MBI31])
  $q = [H31]::VirtualQueryEx($hProc, [IntPtr]([int64]$addr), [ref]$mbi, [uint64]48)
  $b = Get-Bytes $addr 16
  $first = 'unreadable'
  if ($b) { $first = (($b | ForEach-Object { $_.ToString('X2') }) -join ' ') }
  $line = ("[PAGE] 0x{0:X}: state={1} prot=0x{2:X} regionSize=0x{3:X} regionBase=0x{4:X} read={5}" -f $rva, $mbi.State, $mbi.Protect, $mbi.RegionSize, (RvaOf $mbi.BaseAddress), $first)
  Write-Output $line
  if (-not $b) {
    # try a temporary protection change (PAGE_EXECUTE_READWRITE = 0x40)
    $old = [uint32]0
    $ok = [H31]::ProtectEx($hProc, [IntPtr]([int64]$addr), 16, 0x40, [ref]$old)
    if ($ok) {
      $b2 = Get-Bytes $addr 16
      $first2 = 'still unreadable'
      if ($b2) { $first2 = (($b2 | ForEach-Object { $_.ToString('X2') }) -join ' ') }
      Write-Output ("[PAGE]   0x{0:X}: VirtualProtectEx OK (oldProt=0x{1:X}) -> read={2}" -f $rva, $old, $first2)
      [void][H31]::ProtectEx($hProc, [IntPtr]([int64]$addr), 16, $old, [ref]$old)
      [void][H31]::FlushIcache($hProc, [IntPtr]([int64]$addr), 16)
    } else {
      Write-Output ("[PAGE]   0x{0:X}: VirtualProtectEx FAILED err={1}" -f $rva, [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
  }
}
foreach ($r in $rv) { Probe-Rva ([uint64]$r) }

# ---------------------------------------------------------------- [2] pools
Write-Output "== [2] per-record inventory (who owns a valid handle?) =="
$poolTable = $base + 0x582D150
$famInfo = New-Object System.Collections.ArrayList
for ($f = 0; $f -lt 32; $f++) {
  $p = Get-Qword ($poolTable + [uint64]($f * 8))
  if (-not (Test-Pointer $p)) { continue }
  $el   = Get-Qword ($p + 0x08)
  $capU = Get-Dword ($p + 0x10)
  if ((-not (Test-Pointer $el)) -or ($capU -lt 1) -or ($capU -gt 4096)) { continue }
  [void]$famInfo.Add(@{ f = $f; p = $p; el = $el; cap = [int]$capU })
  $eb = Get-Bytes $el ([int]($capU * 16))
  if (-not $eb) { continue }
  $validU398 = 0; $validE398 = 0; $occ = 0
  for ($k = 0; $k -lt [int]$capU; $k++) {
    $o = $k * 16
    if (([int64](Db $eb $o) -band 0x80000000) -eq 0) { continue }
    $occ++
    $rec = Qb $eb ($o + 8)
    if (-not (Test-Pointer $rec)) { continue }
    $h = Get-Qword ($rec + 0x00)
    $unit = Get-Qword ($rec + 0xD8)
    $E = Get-Qword ($rec + 0x08)
    $u398 = 0; $u39F = -1; $vt = 0
    if (Test-Pointer $unit) {
      $vt = Get-Qword $unit
      $u398 = Get-Qword ($unit + 0x398)
      $bb = Get-Bytes ($unit + 0x39F) 1
      if ($bb) { $u39F = [int]$bb[0] }
    }
    $e398 = 0; $e39F = -1; $e10 = 0; $e18 = 0
    if (Test-Pointer $E) {
      $e398 = Get-Qword ($E + 0x398)
      $bb = Get-Bytes ($E + 0x39F) 1
      if ($bb) { $e39F = [int]$bb[0] }
      $e10 = Get-Dword ($E + 0x10)
      $e18 = Get-Dword ($E + 0x18)
    }
    $u398ok = (($u398 -band $U64_LO32) -eq [uint64]$k)
    $e398ok = (($e398 -band $U64_LO32) -eq [uint64]$k)
    if ($u398ok) { $validU398++ }
    if ($e398ok) { $validE398++ }
    Write-Output ("[INV] fam={0} slot={1,3} rec=0x{2:X} handle=0x{3:X16} unit=0x{4:X} vt=+0x{5:X} u+398=0x{6:X16}{7} u+39F=0x{8:X2}  E=0x{9:X} E+10=0x{10:X} E+18=0x{11:X} E+398=0x{12:X16}{13} E+39F=0x{14:X2}" -f `
      $f, $k, $rec, $h, $unit, (RvaOf $vt), $u398, $(if ($u398ok) { ' <== MATCH' } else { '' }), $u39F, $E, $e10, $e18, $e398, $(if ($e398ok) { ' <== MATCH' } else { '' }), $e39F)
  }
  Write-Output ("[INV] fam={0}: occupied={1} handles-at-unit+398 matching slot: {2}  at E+398: {3}" -f $f, $occ, $validU398, $validE398)
}

# ---------------------------------------------------------------- [3] deep dumps
Write-Output "== [3] deep dumps for the player and the target =="
function Locate-Record([uint64]$gLo, [uint64]$gHi) {
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
      if (((Get-Qword ($rec + 0x18)) -eq $gLo) -and ((Get-Qword ($rec + 0x20)) -eq $gHi)) {
        return @{ rec = $rec; fam = [int]$fi.f; slot = $k }
      }
    }
  }
  return $null
}
$pl = Locate-Record $pg $pgh
$tl = $null
if (($tg -ne 0) -or ($tgh -ne 0)) { $tl = Locate-Record $tg $tgh }
foreach ($pair in @(@{ tag = 'PLAYER'; loc = $pl }, @{ tag = 'TARGET'; loc = $tl })) {
  $tag = $pair.tag; $loc = $pair.loc
  if ($null -eq $loc) { Write-Output ("[{0}] record not found" -f $tag); continue }
  $rec = [uint64]$loc.rec; $slot = [int]$loc.slot; $fam = [int]$loc.fam
  Write-Output ("---- {0}: rec=0x{1:X} slot={2} fam={3} ----" -f $tag, $rec, $slot, $fam)
  Show-Head $rec 0x40 ("{0}-REC" -f $tag)
  $unit = Get-Qword ($rec + 0xD8)
  if (Test-Pointer $unit) {
    Write-Output ("[{0}] unit=0x{1:X} vt=+0x{2:X}" -f $tag, $unit, (RvaOf (Get-Qword $unit)))
    Show-Head $unit 0x40 ("{0}-UNIT" -f $tag)
    Find-ObjHandle $unit 0x4000 $slot $fam ("{0}-UNIT" -f $tag)
  }
  $E = Get-Qword ($rec + 0x08)
  if (Test-Pointer $E) {
    Write-Output ("[{0}] E=0x{1:X} vt=+0x{2:X}" -f $tag, $E, (RvaOf (Get-Qword $E)))
    Show-Head $E 0x60 ("{0}-E" -f $tag)
    Find-ObjHandle $E 0x4000 $slot $fam ("{0}-E" -f $tag)
    $E2 = Get-Qword ($E + 0x08)
    if (Test-Pointer $E2) {
      Write-Output ("[{0}] E2=*(E+8)=0x{1:X} vt=+0x{2:X}" -f $tag, $E2, (RvaOf (Get-Qword $E2)))
      Show-Head $E2 0x40 ("{0}-E2" -f $tag)
      Find-ObjHandle $E2 0x4000 $slot $fam ("{0}-E2" -f $tag)
    }
  }
}

# ---------------------------------------------------------------- [4] window scan
Write-Output "== [4] window scan +/-0x200000 around the player record =="
if ($null -ne $pl) {
  $rec = [uint64]$pl.rec; $slot = [int]$pl.slot; $fam = [int]$pl.fam
  $unit = Get-Qword ($rec + 0xD8)
  $hWant = ([uint64]$fam -shl 56) -bor [uint64]$slot
  Write-Output ("[WIN] looking for qwords == rec 0x{0:X}, unit 0x{1:X}, handle 0x{2:X16}" -f $rec, $unit, $hWant)
  $start = $rec - 0x200000
  $hRec = 0; $hUnit = 0; $hHandle = 0
  for ($w = 0; $w -lt 0x400000; $w += 0x10000) {
    $a = $start + [uint64]$w
    $b = Get-Bytes $a 0x10000
    if ($null -eq $b) { continue }
    for ($i = 0; $i + 8 -le $b.Length; $i += 8) {
      $v = Qb $b $i
      if ($v -eq $rec -and $hRec -lt 6) {
        Write-Output ("[WIN] rec pointer @ 0x{0:X} (rec{1}0x{2:X})" -f ($a + [uint64]$i), $(if (($a + [uint64]$i) -ge $rec) { '+' } else { '-' }), [Math]::Abs([int64](($a + [uint64]$i) - $rec)))
        $hRec++
      }
      elseif (($v -eq $unit) -and ($unit -ne 0) -and ($hUnit -lt 6)) {
        Write-Output ("[WIN] unit pointer @ 0x{0:X} (rec{1}0x{2:X})" -f ($a + [uint64]$i), $(if (($a + [uint64]$i) -ge $rec) { '+' } else { '-' }), [Math]::Abs([int64](($a + [uint64]$i) - $rec)))
        $hUnit++
      }
      elseif (($v -eq $hWant) -and ($hHandle -lt 6)) {
        Write-Output ("[WIN] packed handle @ 0x{0:X} (rec{1}0x{2:X})" -f ($a + [uint64]$i), $(if (($a + [uint64]$i) -ge $rec) { '+' } else { '-' }), [Math]::Abs([int64](($a + [uint64]$i) - $rec)))
        $hHandle++
      }
    }
  }
  Write-Output ("[WIN] totals: recPtr={0} unitPtr={1} handle={2} (caps 6 each)" -f $hRec, $hUnit, $hHandle)
}

# ---------------------------------------------------------------- [5] registry v2
Write-Output "== [5] API registry v2 (FIXED: scan for the name-string POINTER) =="
$anchors = @(
  @{ n = 'CastSpellByName';  str = 0x5120A70; hint = 0x59096B0 },
  @{ n = 'MoveForwardStart'; str = 0x5071350; hint = 0x58CF320 },
  @{ n = 'GetActionInfo';    str = 0x512BF08; hint = 0x590CB20 }
)
foreach ($an in $anchors) {
  $strAbs = $base + [uint64]$an.str
  $hintAbs = $base + [uint64]$an.hint
  $entry = [uint64]0
  # search a 0x2000 window around the hint for a qword == strAbs
  $s = $hintAbs - 0x1000
  for ($w = 0; $w -lt 0x2000; $w += 0x1000) {
    $b = Get-Bytes ($s + [uint64]$w) 0x1000
    if ($null -eq $b) { continue }
    for ($i = 0; $i + 8 -le $b.Length; $i += 8) {
      if ((Qb $b $i) -eq $strAbs) { $entry = $s + [uint64]$w + [uint64]$i; break }
    }
    if ($entry -ne 0) { break }
  }
  if ($entry -eq 0) { Write-Output ("[REG2] {0}: name pointer NOT found near hint 0x{1:X}" -f $an.n, $an.hint); continue }
  Write-Output ("[REG2] {0}: entry @ base+0x{1:X} (name ptr at that slot; fn follows at +0x8)" -f $an.n, (RvaOf $entry))
  $entries = New-Object System.Collections.ArrayList
  $i = 0
  while ($i -lt 300) {
    $a = $entry + [uint64]($i * 16)
    $nq = Get-Qword $a
    $fq = Get-Qword ($a + 8)
    if ((-not (Test-ModulePtr $nq)) -or (-not (Test-ModulePtr $fq))) { break }
    $nm = Read-AsciiAt $nq 40
    if ($nm.Length -lt 3) { break }
    if ((RvaOf $fq) -gt 0x4000000) { break }
    [void]$entries.Add(@{ off = ($i * 16); n = $nm; fn = (RvaOf $fq); neg = $false })
    $i++
  }
  $j = 1
  while ($j -lt 300) {
    $a = $entry - [uint64]($j * 16)
    $nq = Get-Qword $a
    $fq = Get-Qword ($a + 8)
    if ((-not (Test-ModulePtr $nq)) -or (-not (Test-ModulePtr $fq))) { break }
    $nm = Read-AsciiAt $nq 40
    if ($nm.Length -lt 3) { break }
    if ((RvaOf $fq) -gt 0x4000000) { break }
    [void]$entries.Insert(0, @{ off = -($j * 16); n = $nm; fn = (RvaOf $fq); neg = $true })
    $j++
  }
  Write-Output ("[REG2] {0}: extent {1} entries (fwd={2} bwd={3})" -f $an.n, $entries.Count, $i, ($j - 1))
  foreach ($e in $entries) {
    $sign = '+'; if ($e.neg) { $sign = '-' }
    Write-Output ("[REG2]   {0}{1:X4}  {2,-34} -> 0x{3:X}" -f $sign, [Math]::Abs([int64]$e.off), $e.n, $e.fn)
  }
}

# ---------------------------------------------------------------- [6] stats
Write-Output "== [6] stat block 0x1E00..0x1E80 (level eyeball) =="
foreach ($pair in @(@{ tag = 'PLAYER'; loc = $pl }, @{ tag = 'TARGET'; loc = $tl })) {
  if ($null -eq $pair.loc) { continue }
  $rec = [uint64]$pair.loc.rec
  $D = Get-Qword ($rec + 0xD8)
  if (-not (Test-Pointer $D)) { continue }
  $txt = ''
  for ($off = 0x1E00; $off -lt 0x1E80; $off += 4) {
    $txt += (' +0x{0:X}={1}' -f $off, (Get-Dword ($D + $off)))
  }
  Write-Output ("[STATS-{0}] {1}" -f $pair.tag, $txt)
  Write-Output ("[STATS-{0}] health={1}/{2} power={3}/{4} unitFlags=0x{5:X}" -f $pair.tag, (Get-Dword ($D+0x1E30)), (Get-Dword ($D+0x1E38)), (Get-Dword ($D+0x1BC8)), (Get-Dword ($D+0x1BD0)), (Get-Dword ($D+0x1E60)))
  Write-Output ("[STATS-{0}] EYEBALL: compare the 1..80 values above with your level (character sheet) and the target's level" -f $pair.tag)
}

[void][H31]::CloseHandle($hProc)
Write-Output "[DONE]"
