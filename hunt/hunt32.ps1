# =====================================================================
#  hunt32.ps1 - WoWB x64 (1.60.1 / build 70291) live RE, round 32
#  Theme: UNBLOCK PHASE 3 -- Lua execution.
# =====================================================================
#  hunt31 already returned the full live {name,fn} tables for the
#  spellbook / movement / action-bar groups (98 handlers). What is still
#  missing before the bot can run a single line of Lua is:
#    (1) does the page protection flip mean ENCRYPTED or merely PROTECTED?
#        -> decides whether we may just VirtualProtectEx a handler page to
#           RX and remote-CALL it (Executor64.ExecuteRaw is already written)
#           or whether only the game's own decrypt-on-execute can run it.
#    (2) the Lua C-API wrapper set (settop/pushlstring/pushstring/
#        pushcclosure/pcall/getglobal/tostring) -> the push-string + pcall +
#        RunScript recipe for arbitrary Lua from a remote thread.
#    (3) RunScript / RunMacro / UnitHealth / UnitName / targeting RVAs.
#    (4) the exact "this record is a real unit" rule (back-pointer check).
#
#  Sections:
#   [1] live vs static-image byte compare per RVA.  Compares the live bytes
#       (unlocking PAGE_NOACCESS with VirtualProtectEx, then restoring the
#       original protection) against the same RVA in
#       (a) the decoded dump on the Desktop and (b) the original WowB.exe.
#       live == decoded  -> plaintext, we may flip pages ourselves.
#       live == original -> still packed; the game must decrypt it.
#   [2] 40 KB hex dump of the Lua C-API cluster 0x4718000..0x4722000
#       (live) -> file C:\ghidra-scripts\hunt32-code.txt
#   [3] FrameScript registration-table scan over the data windows; prints
#       ~48 names the bot needs with their handler RVAs, the full extent of
#       every table that contains one of them, and dumps the first 0x400
#       bytes of RunScript / RunMacro / UnitHealth / CastSpellByName
#       (live + decoded file) into hunt32-code.txt.
#   [4] unit inventory v2: for every occupied record, validate rec+0xD8 as a
#       unit object (aligned + *(unit+8)==rec + GUID pair match) and print a
#       vtable histogram -> kills the guesswork about "unit" pointers.
#   [5] page-protection timeline (interactive, 24 s): samples 5 probe pages
#       every 0.5 s while you press keys -> proves decrypt-on-execute.
#
# Run ELEVATED, in-game (character logged in), an NPC targeted:
#   powershell -ExecutionPolicy Bypass -File C:\ghidra-scripts\hunt32.ps1 | Tee-Object C:\ghidra-scripts\hunt32-out.txt
# The raw hex goes to C:\ghidra-scripts\hunt32-code.txt (not the console).
# =====================================================================
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct MBI32 {
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
public static class H32 {
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, out IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern ulong VirtualQueryEx(IntPtr h, IntPtr addr, out MBI32 mbi, ulong len);
  [DllImport("kernel32.dll", EntryPoint="VirtualProtectEx", SetLastError=true)]
  static extern bool VirtualProtectExRaw(IntPtr h, IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);
  public static bool ProtectEx(IntPtr h, IntPtr addr, int size, uint newProtect, out uint oldProtect) {
    return VirtualProtectExRaw(h, addr, (UIntPtr)(ulong)size, newProtect, out oldProtect);
  }
  public static bool Query(IntPtr h, ulong addr, out ulong rbase, out ulong rsize, out uint state, out uint prot) {
    MBI32 m;
    rbase = 0; rsize = 0; state = 0; prot = 0;
    if (VirtualQueryEx(h, (IntPtr)(long)addr, out m, 48) == 0) { return false; }
    rbase = m.BaseAddress; rsize = m.RegionSize; state = m.State; prot = m.Protect;
    return true;
  }
}
public static class H32Scan {
  // Walk a raw data window for {namePtr, fnPtr} qword pairs; returns how many
  // candidate entries were stored (entryRva/namePtr/fnPtr arrays must be >= cap).
  public static int CollectEntries(byte[] hay, int hayLen, long winRva, ulong imgBase, ulong imgEnd,
                                   ulong strLo, ulong strHi, ulong txtLo, ulong txtHi,
                                   long[] outEntryRva, ulong[] outNamePtr, ulong[] outFnPtr, int cap) {
    int count = 0;
    for (int i = 0; i + 16 <= hayLen; i += 8) {
      ulong np = BitConverter.ToUInt64(hay, i);
      if (np < strLo || np >= strHi) { continue; }
      ulong fp = BitConverter.ToUInt64(hay, i + 8);
      if (fp < txtLo || fp >= txtHi) { continue; }
      if (count >= cap) { break; }
      outEntryRva[count] = winRva + i;
      outNamePtr[count] = np;
      outFnPtr[count] = fp;
      count++;
    }
    return count;
  }
  public static long FindBytes(byte[] hay, int hayLen, byte[] needle, int start) {
    int n = needle.Length, last = hayLen - n;
    if (last < 0) { return -1; }
    byte b0 = needle[0];
    for (int i = start; i <= last; i++) {
      if (hay[i] != b0) { continue; }
      int j = 1;
      while (j < n && hay[i + j] == needle[j]) { j++; }
      if (j == n) { return i; }
    }
    return -1;
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
$hProc = [H32]::OpenProcess(0x0410, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) { throw ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Write-Output ("[HDR] pid={0} base=0x{1:X} moduleSize=0x{2:X}" -f $proc.Id, $base, $modSize)
Write-Output "[BANNER] sections [1]-[4] are passive. section [5] is INTERACTIVE: it samples code-page"
Write-Output "[BANNER] protections for 24 s while you press keys. Read its banner before you act."

# ---------------------------------------------------------------- helpers
$U64_LO32 = [uint64]4294967295

function Get-Bytes([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H32]::ReadProcessMemory($hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
  if ($got.ToInt64() -ne $n) { return $null }
  return , $buf
}
function Get-Qword([uint64]$addr) { $b = Get-Bytes $addr 8; if ($null -eq $b) { return [uint64]0 }; return [BitConverter]::ToUInt64($b, 0) }
function Get-Dword([uint64]$addr) { $b = Get-Bytes $addr 4; if ($null -eq $b) { return [uint32]0 }; return [BitConverter]::ToUInt32($b, 0) }
function Get-Byte([uint64]$addr) { $b = Get-Bytes $addr 1; if ($null -eq $b) { return -1 }; return [int]$b[0] }
function Test-Pointer([uint64]$p) { return (($p -gt 0x10000) -and ($p -lt [uint64]0x7FFFFFFFFFFF) -and (($p % 8) -eq 0)) }
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
function Hex-Of([byte[]]$b, [int]$n) {
  if ($null -eq $b) { return 'none' }
  $m = [Math]::Min($n, $b.Length)
  $s = ''
  for ($i = 0; $i -lt $m; $i++) { $s += $b[$i].ToString('X2') }
  return $s
}
function RvaOf([uint64]$p) {
  if ($p -lt $base) { return [int64](-1) }
  $d = $p - $base
  if ($d -gt $modSize) { return [int64](-2) }
  return [int64]$d
}
function RvaStr([uint64]$p) {
  $r = RvaOf $p
  if ($r -lt 0) { return 'OUT' }
  return ('0x{0:X}' -f $r)
}
function Get-Prot([uint64]$addr) {
  $rb = [uint64]0; $rs = [uint64]0; $st = [uint32]0; $pr = [uint32]0
  if (-not [H32]::Query($hProc, $addr, [ref]$rb, [ref]$rs, [ref]$st, [ref]$pr)) { return -1 }
  return [int]$pr
}
function PageOf([uint64]$a) { return ($a -band [uint64]0xFFFFFFFFFFFFF000) }

$codePath = 'C:\ghidra-scripts\hunt32-code.txt'
$script:CodeLines = New-Object System.Collections.ArrayList
function Code-Line([string]$s) { [void]$script:CodeLines.Add($s) }
function Code-DumpLive([uint64]$addr, [int]$size, [string]$tag) {
  Code-Line ("### LIVE {0} @ 0x{1:X}" -f $tag, $addr)
  for ($off = 0; $off -lt $size; $off += 0x800) {
    $n = [Math]::Min(0x800, $size - $off)
    $b = Get-Bytes ($addr + [uint64]$off) $n
    if ($null -eq $b) { Code-Line ("MISS 0x{0:X}" -f ($addr - $base + [uint64]$off)); continue }
    for ($i = 0; $i -lt $n; $i += 32) {
      $s = ("0x{0:X} " -f ((([uint64]$addr - $base) + [uint64]$off) + [uint64]$i))
      $lim = [Math]::Min(32, $n - $i)
      for ($j = 0; $j -lt $lim; $j++) { $s += $b[$i + $j].ToString('X2') }
      Code-Line $s
    }
  }
}

# ---------------------------------------------------------------- PE files
function Open-PeFile([string]$path) {
  if ([string]::IsNullOrEmpty($path)) { return $null }
  if (-not (Test-Path $path)) { return $null }
  try {
    $fs = [System.IO.File]::OpenRead($path)
    $hdr = New-Object byte[] 0x1000
    $got = $fs.Read($hdr, 0, 0x1000)
    if ($got -lt 0x1000) { $fs.Close(); return $null }
    $peOff = [BitConverter]::ToInt32($hdr, 0x3C)
    if ($peOff -le 0 -or $peOff -gt 0x400) { $fs.Close(); return $null }
    $nsec = [BitConverter]::ToUInt16($hdr, $peOff + 6)
    $optSize = [BitConverter]::ToUInt16($hdr, $peOff + 0x14)
    $secOff = $peOff + 0x18 + $optSize
    if ($nsec -lt 1 -or $nsec -gt 96 -or ($secOff + $nsec * 0x28) -gt 0x1000) { $fs.Close(); return $null }
    $secs = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $nsec; $i++) {
      $o = $secOff + $i * 0x28
      $nm = (([Text.Encoding]::ASCII.GetString($hdr, $o, 8)) -split "`0")[0]
      $vs = [BitConverter]::ToUInt32($hdr, $o + 8)
      $va = [BitConverter]::ToUInt32($hdr, $o + 0xC)
      $rs = [BitConverter]::ToUInt32($hdr, $o + 0x10)
      $rp = [BitConverter]::ToUInt32($hdr, $o + 0x14)
      [void]$secs.Add(@{ n = $nm; va = [uint64]$va; vs = [uint64]$vs; rs = [uint64]$rs; rp = [int64]$rp })
    }
    return @{ path = $path; fs = $fs; secs = $secs }
  } catch { return $null }
}
function Get-FileBytesAt($pe, [uint64]$rva, [int]$n) {
  if ($null -eq $pe) { return $null }
  foreach ($s in $pe.secs) {
    if ($rva -ge $s.va -and $rva -lt ($s.va + $s.vs)) {
      $delta = $rva - $s.va
      if ($delta -ge $s.rs) { return $null }
      try {
        [void]$pe.fs.Seek(($s.rp + [int64]$delta), 'Begin')
        $buf = New-Object byte[] $n
        $gotf = $pe.fs.Read($buf, 0, $n)
        if ($gotf -ne $n) { return $null }
        return , $buf
      } catch { return $null }
    }
  }
  return $null
}

$decPaths = @(
  (Join-Path $env:USERPROFILE 'Desktop\wowb-decoded.exe'),
  (Join-Path $env:USERPROFILE 'Desktop\wowb_decoded.exe'),
  'C:\ghidra-scripts\wowb-decoded.exe',
  'C:\ghidra-projects\wowb-decoded.exe',
  'C:\ghidra-scripts\wowb.exe'
)
$peDec = $null
foreach ($p in $decPaths) {
  $peDec = Open-PeFile $p
  if ($null -ne $peDec) { Write-Output ("[FILE] decoded dump: {0}" -f $peDec.path); break }
}
if ($null -eq $peDec) { Write-Output "[FILE] decoded dump NOT FOUND (looked in Desktop + C:\ghidra-scripts)" }
$peOrig = $null
try { $peOrig = Open-PeFile $proc.MainModule.FileName } catch { }
if ($null -ne $peOrig) { Write-Output ("[FILE] original image: {0}" -f $peOrig.path) } else { Write-Output "[FILE] original image not openable" }
if ($null -ne $peDec) {
  $secNames = ($peDec.secs | ForEach-Object { $_.n }) -join ','
  Write-Output ("[FILE] decoded sections: {0}" -f $secNames)
}

# ---------------------------------------------------------------- [0] sanity
Write-Output ""
Write-Output "== [0] sanity =="
$uL = Get-Qword ($base + 0x7B76AB8)
Write-Output ("[SAN] lua_State global [base+0x7B76AB8] = 0x{0:X}" -f $uL)
if (Test-Pointer $uL) {
  $hb = Get-Bytes $uL 0x80
  if ($null -ne $hb) { Code-Line ("### LUA STATE L=0x{0:X}" -f $uL); Code-DumpLive $uL 0x100 'lua_State' }
}
Write-Output ("[SAN] player guid lo = 0x{0:X}  name='{1}'" -f (Get-Dword ($base + 0x61C72F0)), (Read-AsciiAt ($base + 0x61C7300) 32))
Write-Output ("[SAN] target guid lo = 0x{0:X}" -f (Get-Dword ($base + 0x7BDFF38)))

# ---------------------------------------------------------------- [1] compare
Write-Output ""
Write-Output "== [1] live vs static-image bytes per RVA (is a NOACCESS page ENCRYPTED?) =="
$cmpList = @(
  @{ rva = 0x23C8BE0; n = 'MoveForwardStart' },
  @{ rva = 0x23C5560; n = 'JumpOrAscendStart' },
  @{ rva = 0x2C24040; n = 'CastSpellByName' },
  @{ rva = 0x2C2B5E0; n = 'GetNumCompanions' },
  @{ rva = 0x2CC2DB0; n = 'RunMacro' },
  @{ rva = 0x2CDA340; n = 'UseAction' },
  @{ rva = 0x1FC37F0; n = 'FnGuidToUnitChecked' },
  @{ rva = 0xAE96A0;  n = 'FnApiRegister' },
  @{ rva = 0x2BBB7F0; n = 'FnEntityGetGuid' },
  @{ rva = 0x1D98820; n = 'RegisterAll' },
  @{ rva = 0x471A960; n = 'lua_gettop' },
  @{ rva = 0x471BC20; n = 'pushlstring cand' },
  @{ rva = 0x471B800; n = 'pushcclosure cand' }
)
$flips = 0; $matchDec = 0; $matchOrig = 0
foreach ($row in $cmpList) {
  $a = $base + [uint64]$row.rva
  $prot = Get-Prot $a
  $live = Get-Bytes $a 32
  $note = ''
  if ($null -eq $live -and $prot -eq 1) {
    $oldp = [uint32]0
    if ([H32]::ProtectEx($hProc, [IntPtr]([int64]$a), 0x1000, 0x20, [ref]$oldp)) {
      $live = Get-Bytes $a 32
      $flips++
      $note = (" FLIP:old=0x{0:X}" -f $oldp)
      $oldp2 = [uint32]0
      [void][H32]::ProtectEx($hProc, [IntPtr]([int64]$a), 0x1000, $oldp, [ref]$oldp2)
      $note += (" restore->0x{0:X}" -f (Get-Prot $a))
    } else {
      $note = (" FLIP-FAILED err={0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
  }
  $dec = Get-FileBytesAt $peDec ([uint64]$row.rva) 32
  $ori = Get-FileBytesAt $peOrig ([uint64]$row.rva) 32
  $liveHex = Hex-Of $live 16
  $decHex = Hex-Of $dec 16
  $oriHex = Hex-Of $ori 16
  $fl = ''
  if ($null -ne $live) {
    if ($liveHex -eq $decHex) { $fl += ' live==DECODED'; $matchDec++ }
    if ($liveHex -eq $oriHex) { $fl += ' live==ORIGINAL'; $matchOrig++ }
    if ($fl -eq '') { $fl = ' live==NEITHER' }
  }
  Write-Output ("[CMP] 0x{0:X} {1,-20} prot=0x{2:X}{3}" -f $row.rva, $row.n, $prot, $note)
  Write-Output ("[CMP]      live= {0}" -f $liveHex)
  Write-Output ("[CMP]      decd= {0}" -f $decHex)
  Write-Output ("[CMP]      orig= {0}  {1}" -f $oriHex, $fl)
}
Write-Output ("[CMP] summary: page-flips={0} live==decoded:{1} live==original:{2} (of {3})" -f $flips, $matchDec, $matchOrig, $cmpList.Count)

# ---------------------------------------------------------------- [2] lua dump
Write-Output ""
Write-Output "== [2] Lua C-API cluster dump 0x4718000..0x4722000 -> hunt32-code.txt =="
Code-Line "### hunt32 code dump"
Code-Line ("### base=0x{0:X}" -f $base)
Code-DumpLive ($base + 0x4718000) 0xA000 'lua-cluster'
Write-Output "[DUMP] 0x4718000..0x4722000 (0xA000 bytes) written to hunt32-code.txt"

# ---------------------------------------------------------------- [3] api scan
Write-Output ""
Write-Output "== [3] FrameScript registration-table scan =="
$targetNames = @(
  'RunScript','RunMacro','RunMacroText','RunBinding','GetTime','GetFramerate','GetCVar','SetCVar',
  'UnitHealth','UnitHealthMax','UnitPower','UnitPowerMax','UnitLevel','UnitName','UnitClass','UnitGUID',
  'UnitExists','UnitIsPlayer','UnitIsDead','UnitIsDeadOrGhost','UnitIsEnemy','UnitCanAttack','UnitAffectingCombat',
  'UnitCastingInfo','UnitChannelInfo','UnitBuff','UnitDebuff','UnitAura','UnitPosition','UnitInRange','UnitReaction','UnitIsUnit',
  'TargetUnit','TargetNearestEnemy','TargetNearestFriend','TargetLastTarget','TargetLastEnemy','ClearTarget',
  'AssistUnit','AttackTarget','StartAttack','StopAttack','FollowUnit',
  'CastSpell','CastSpellByName','CastSpellByID','UseAction','UseContainerItem','PickupContainerItem',
  'GetContainerItemInfo','GetActionCooldown','IsCurrentAction','IsUsableAction','GetActionInfo',
  'SpellStopCasting','SpellStopTargeting','InteractUnit','RepopMe','RetrieveCorpse','AcceptResurrect',
  'SelectGossipOption','GetGossipOptions','AcceptQuest','SelectQuestLogEntry','GetPlayerMapPosition',
  'IsSpellKnown','GetSpellInfo','IsMouselooking','GetMouseFocus','GetKeyState','UnitTokenFromGUID','GetName'
)
$strLo = $base + [uint64]0x4F00000
$strHi = $base + [uint64]0x6000000
$txtLo = $base + [uint64]0x1000
$txtHi = $base + [uint64]0x4F00000
$windows = @(
  @{ s = [uint64]0x5800000; len = 0x200000 },
  @{ s = [uint64]0x8200000; len = 0x100000 },
  @{ s = [uint64]0x83F0000; len = 0x110000 }
)
$cap = 8192
$eRva = New-Object 'long[]' $cap
$eName = New-Object 'ulong[]' $cap
$eFn = New-Object 'ulong[]' $cap
$found = New-Object System.Collections.ArrayList
foreach ($w in $windows) {
  Write-Output ("[SCAN] window base+0x{0:X} size=0x{1:X}" -f $w.s, $w.len)
  $chunk = 0x10000
  for ($o = 0; $o -lt $w.len; $o += $chunk) {
    $b = Get-Bytes ($base + $w.s + [uint64]$o) $chunk
    if ($null -eq $b) { continue }
    $n = [H32Scan]::CollectEntries($b, $b.Length, [int64]($w.s + [uint64]$o), $base, ($base + $modSize), $strLo, $strHi, $txtLo, $txtHi, $eRva, $eName, $eFn, $cap)
    for ($k = 0; $k -lt $n; $k++) {
      [void]$found.Add(@{ rva = $eRva[$k]; np = $eName[$k]; fn = $eFn[$k] })
    }
  }
}
Write-Output ("[SCAN] candidate entries: {0}" -f $found.Count)
$tbl = New-Object System.Collections.ArrayList
foreach ($f in $found) {
  $nm = Read-AsciiAt $f.np 40
  if ($nm.Length -lt 3) { continue }
  [void]$tbl.Add([pscustomobject]@{ rva = [int64]$f.rva; n = $nm; fn = (RvaOf $f.fn); np = $f.np })
}
Write-Output ("[SCAN] entries with a readable name: {0}" -f $tbl.Count)
$sorted = $tbl | Sort-Object -Property rva
$groups = New-Object System.Collections.ArrayList
$cur = $null
foreach ($e in $sorted) {
  if ($null -ne $cur -and ($e.rva -eq ($cur.last + 16))) {
    [void]$cur.items.Add($e); $cur.last = $e.rva
  } else {
    $cur = @{ base = $e.rva; last = $e.rva; items = (New-Object System.Collections.ArrayList) }
    [void]$cur.items.Add($e)
    [void]$groups.Add($cur)
  }
}
Write-Output ("[SCAN] tables (contiguous runs): {0}" -f $groups.Count)
foreach ($g in $groups) {
  if ($g.items.Count -lt 2) { continue }
  Write-Output ("[TBL] base=0x{0:X} entries={1} first={2} last={3}" -f $g.base, $g.items.Count, $g.items[0].n, $g.items[$g.items.Count - 1].n)
}
Write-Output ""
Write-Output "-- target-name lookup --"
$lookup = @{}
foreach ($e in $tbl) { if (-not $lookup.ContainsKey($e.n)) { $lookup[$e.n] = $e } }
foreach ($nm in $targetNames) {
  if ($lookup.ContainsKey($nm)) {
    $e = $lookup[$nm]
    $protFn = Get-Prot ($base + [uint64]$e.fn)
    Write-Output ("[API] {0,-22} -> 0x{1:X}  (entry base+0x{2:X}, fn-page prot=0x{3:X})" -f $nm, $e.fn, $e.rva, $protFn)
  } else {
    Write-Output ("[API] {0,-22} -> NOT FOUND" -f $nm)
  }
}
Write-Output ""
Write-Output "-- full dump of the tables that contain a target name --"
$wantedTables = New-Object System.Collections.ArrayList
foreach ($g in $groups) {
  if ($g.items.Count -lt 3) { continue }
  $hit = $false
  foreach ($it in $g.items) { if ($targetNames -contains $it.n) { $hit = $true; break } }
  if ($hit) { [void]$wantedTables.Add($g) }
  if ($wantedTables.Count -ge 25) { break }
}
$tblLines = 0
foreach ($g in $wantedTables) {
  Write-Output ("[TBL] ---- base=0x{0:X} entries={1} ----" -f $g.base, $g.items.Count)
  $shown = 0
  foreach ($it in $g.items) {
    if ($shown -ge 120) { Write-Output ("[TBL]   ... truncated ({0} entries)" -f $g.items.Count); break }
    if ($tblLines -ge 1500) { Write-Output "[TBL]   ... GLOBAL CAP reached"; break }
    Write-Output ("[TBL]   +0x{0:X3} {1,-34} -> 0x{2:X}" -f ($it.rva - $g.base), $it.n, $it.fn)
    $shown++
    $tblLines++
  }
  if ($tblLines -ge 1500) { break }
}
Write-Output ""
Write-Output "-- handler body dumps for the Phase-3 targets --"
foreach ($nm in @('RunScript','RunMacro','UnitHealth','CastSpellByName')) {
  if (-not $lookup.ContainsKey($nm)) { continue }
  $e = $lookup[$nm]
  $fa = $base + [uint64]$e.fn
  $prot = Get-Prot $fa
  $note = ''
  if ($prot -eq 1) {
    $oldp = [uint32]0
    if ([H32]::ProtectEx($hProc, [IntPtr]([int64]$fa), 0x1000, 0x20, [ref]$oldp)) {
      $note = (" (page flipped 0x{0:X}->RX for the read)" -f $oldp)
      Code-DumpLive $fa 0x400 ($nm + '-LIVE')
      $oldp2 = [uint32]0
      [void][H32]::ProtectEx($hProc, [IntPtr]([int64]$fa), 0x1000, $oldp, [ref]$oldp2)
    } else { $note = ' (page NOACCESS and ProtEx failed)' }
  } else {
    Code-DumpLive $fa 0x400 ($nm + '-LIVE')
  }
  $fb = Get-FileBytesAt $peDec ([uint64]$e.fn) 0x400
  if ($null -ne $fb) {
    Code-Line ("### DECODED-FILE {0} @ 0x{1:X}" -f $nm, $e.fn)
    for ($i = 0; $i -lt $fb.Length; $i += 32) {
      $s = ("0x{0:X} " -f ([uint64]$e.fn + [uint64]$i))
      $lim = [Math]::Min(32, $fb.Length - $i)
      for ($j = 0; $j -lt $lim; $j++) { $s += $fb[$i + $j].ToString('X2') }
      Code-Line $s
    }
  }
  Write-Output ("[BODY] {0}: fn=0x{1:X} prot=0x{2:X}{3}" -f $nm, $e.fn, $prot, $note)
}

# ---------------------------------------------------------------- [4] inventory v2
Write-Output ""
Write-Output "== [4] unit inventory v2 (validate rec+0xD8 as a unit object) =="
$poolTable = $base + 0x582D150
$kinds = @{ }
$goodUnits = 0; $badUnits = 0; $backOk = 0; $guidOk = 0; $occTotal = 0
for ($f = 0; $f -lt 32; $f++) {
  $p = Get-Qword ($poolTable + [uint64]($f * 8))
  if (-not (Test-Pointer $p)) { continue }
  $el = Get-Qword ($p + 0x08)
  $capU = Get-Dword ($p + 0x10)
  if ((-not (Test-Pointer $el)) -or ($capU -lt 1) -or ($capU -gt 4096)) { continue }
  $eb = Get-Bytes $el ([int]($capU * 16))
  if ($null -eq $eb) { continue }
  for ($k = 0; $k -lt [int]$capU; $k++) {
    $o = $k * 16
    if (([int64](Db $eb $o) -band 0x80000000) -eq 0) { continue }
    $occTotal++
    $rec = Qb $eb ($o + 8)
    if (-not (Test-Pointer $rec)) { continue }
    $unit = Get-Qword ($rec + 0xD8)
    if (-not (Test-Pointer $unit)) { $badUnits++; continue }
    $vt = Get-Qword $unit
    $kind = 'other'
    $vr = RvaOf $vt
    if ($vr -eq 0x50A7920) { $kind = 'PLAYER' }
    elseif ($vr -eq 0x50CDDC0) { $kind = 'NPC' }
    elseif ($vr -eq 0x50B1838) { $kind = 'KIND_B1838' }
    elseif ($vr -eq 0x50D8258) { $kind = 'KIND_D8258' }
    if (-not $kinds.ContainsKey($kind)) { $kinds[$kind] = 0 }
    $kinds[$kind]++
    $back = (Get-Qword ($unit + 0x08)) -eq $rec
    $g10 = (Get-Qword ($unit + 0x10)) -eq (Get-Qword ($rec + 0x18))
    $g18 = (Get-Qword ($unit + 0x18)) -eq (Get-Qword ($rec + 0x20))
    $isGood = ($kind -ne 'other') -and $back -and $g10 -and $g18
    if ($isGood) { $goodUnits++ } else { $badUnits++ }
    if ($back) { $backOk++ }
    if ($g10 -and $g18) { $guidOk++ }
    $align = 'ok'; if (($unit % 8) -ne 0) { $align = 'MISALIGNED' }
    $extra = ''
    if ($align -eq 'MISALIGNED') {
      $extra = (" vt-4=+{0} vt+4=+{1}" -f (RvaStr (Get-Qword ($unit - 4))), (RvaStr (Get-Qword ($unit + 4))))
    }
    Write-Output ("[INV2] f={0} slot={1,3} rec=0x{2:X} unit=0x{3:X} {4} vt=+0x{5:X} kind={6,-10} back={7} guid={8}" -f `
      $f, $k, $rec, $unit, $align, $vr, $kind, $back, ($g10 -and $g18))
    if ($extra -ne '') { Write-Output ("[INV2]      misaligned extra:{0}" -f $extra) }
    if ($isGood) {
      $h38 = Get-Qword ($unit + 0x38)
      $hMatch = ($h38 -eq (Get-Qword $rec))
      Write-Output ("[INV2]      good unit: lvl(+0x1E40)={0} hp={1}/{2} pow={3} flags=0x{4:X} handle@unit+0x38=0x{5:X} match={6}" -f `
        (Get-Dword ($unit + 0x1E40)), (Get-Dword ($unit + 0x1E30)), (Get-Dword ($unit + 0x1E38)), (Get-Dword ($unit + 0x1BC8)), (Get-Dword ($unit + 0x1E60)), $h38, $hMatch)
    }
  }
}
Write-Output ("[INV2] occupied={0} goodUnits={1} badOrOther={2} backPtrOk={3} guidPairOk={4}" -f $occTotal, $goodUnits, $badUnits, $backOk, $guidOk)
$kindLine = ''
foreach ($kk in $kinds.Keys) { $kindLine += ("{0}={1} " -f $kk, $kinds[$kk]) }
Write-Output ("[INV2] vtable histogram: {0}" -f $kindLine)

# ---------------------------------------------------------------- [5] timeline
Write-Output ""
Write-Output "== [5] page-protection timeline (INTERACTIVE, 24 s) =="
Write-Output "[ACT] During the next 24 seconds please do, in this order:"
Write-Output "[ACT]   0-5s    hold W for about a second (move forward)"
Write-Output "[ACT]   5-10s   press an action-bar key (1)  /  click a spell"
Write-Output "[ACT]   10-15s  jump (space)"
Write-Output "[ACT]   15-20s  hold the RIGHT mouse button and drag (mouselook)"
Write-Output "[ACT]   20-24s  let the character stand still"
$probes = @(
  @{ rva = [uint64]0x23C8BE0; n = 'MoveForwardStart' },
  @{ rva = [uint64]0x2CDA340; n = 'UseAction' },
  @{ rva = [uint64]0x23C5560; n = 'JumpOrAscendStart' },
  @{ rva = [uint64]0x23D1110; n = 'MouselookStart' },
  @{ rva = [uint64]0x2C24040; n = 'CastSpellByName' }
)
$state = @{}
foreach ($p in $probes) { $state[$p.n] = '?' }
$t0 = Get-Date
for ($k = 0; $k -lt 48; $k++) {
  foreach ($p in $probes) {
    $a = $base + $p.rva
    $prot = Get-Prot $a
    $rb = [uint64]0; $rs = [uint64]0; $st = [uint32]0; $pr = [uint32]0
    [void][H32]::Query($hProc, $a, [ref]$rb, [ref]$rs, [ref]$st, [ref]$pr)
    $prs = ('0x{0:X}' -f $prot)
    if ($state[$p.n] -ne $prs) {
      $el = [Math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
      Write-Output ("[TL] T+{0,5}s  {1,-18} prot {2} -> {3}   (regionBase=0x{4:X} size=0x{5:X} state=0x{6:X})" -f $el, $p.n, $state[$p.n], $prs, (RvaOf $rb), $rs, $st)
      $state[$p.n] = $prs
    }
  }
  Start-Sleep -Milliseconds 500
}
Write-Output ""
Write-Output "-- timeline end state --"
foreach ($p in $probes) { Write-Output ("[TL] {0,-18} final prot {1}" -f $p.n, $state[$p.n]) }

# ---------------------------------------------------------------- write code file
try {
  $sw = New-Object System.IO.StreamWriter($codePath, $false, [Text.Encoding]::ASCII)
  foreach ($l in $script:CodeLines) { $sw.WriteLine($l) }
  $sw.Close()
  Write-Output ("[DUMP] {0} lines written to {1}" -f $script:CodeLines.Count, $codePath)
} catch {
  Write-Output ("[DUMP] could not write {0}: {1}" -f $codePath, $_.Exception.Message)
  Write-Output "[DUMP] ---- inline fallback (code dump follows) ----"
  foreach ($l in $script:CodeLines) { Write-Output $l }
}

if ($null -ne $peDec) { try { $peDec.fs.Close() } catch { } }
if ($null -ne $peOrig) { try { $peOrig.fs.Close() } catch { } }
[void][H32]::CloseHandle($hProc)
Write-Output "[DONE]"
