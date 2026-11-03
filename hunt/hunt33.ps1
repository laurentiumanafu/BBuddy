# =====================================================================
#  hunt33.ps1 - WoWB x64 (1.60.1 / build 70291) live RE, round 33
#  Theme: close the Phase-3 gate. Fixes the two hunt32 bugs and answers
#         the one question that still blocks remote code execution.
# =====================================================================
#  What hunt32 already proved (do not re-prove):
#    * 9 of 13 probed RVAs: live bytes == Desktop\wowb-decoded.exe bytes,
#      0 of 13 == the shipped WowB.exe bytes -> the shipped .text is
#      packed, the decoded dump is the plaintext ground truth.
#    * the handler pages the client had already executed were
#      PAGE_EXECUTE_READ (0x20), the ones it had NOT executed yet were
#      PAGE_NOACCESS (0x1) - decrypt-on-execute, and the decryption is
#      PERMANENT for that process (that is why a second interactive run
#      showed no more transitions: nothing left to decrypt).
#    * rec+0xD8 validates structurally: back-pointer + GUID pair ->
#      37 real units of 89 occupied records.
#
#  hunt32's two bugs (both fixed here):
#    BUG 1: OpenProcess asked for 0x0410 = QUERY_INFORMATION|VM_READ.
#           VirtualProtectEx needs PROCESS_VM_OPERATION (0x8) as well, so
#           every flip failed with err=5 ACCESS_DENIED. Now 0x0428.
#    BUG 2: New-Object 'ulong[]' -> "Cannot find type [ulong[]]" (the C#
#           alias is not a PowerShell type name) -> the arrays were $null
#           -> every CollectEntries call threw NullReferenceException ->
#           the registry scan found 0 entries. Now System.UInt64[].
#           (The static API tables were extracted offline instead - see
#           hunt/api_map.txt - so this run only cross-checks them.)
#
#  NEW QUESTION this run answers:
#    What is inside a page that is STILL PAGE_NOACCESS - plaintext or
#    ciphertext? If plaintext, Executor64 can VirtualProtectEx any handler
#    to RX and call it directly = Phase 3 unblocked with no hook at all.
#    If ciphertext, only pages the client has already executed may be
#    called (warm them up by simulating input first).
#
#  Sections:
#   [1] cold-page inventory + THE FLIP TEST (correct rights). For every
#       candidate RVA: protection, readable?, flip->RX->read->restore,
#       live bytes, packed-file bytes at the same RVA, and a verdict.
#   [2] wide Lua library dump 0x4700000..0x4740000 (256 KB) -> code file.
#       hunt32's 40 KB window identified lua_pcall (0x471B5C0) and
#       luaD_protectedparser (0x4719A80) but NOT lua_load / the lauxlib
#       loader (nobody in the window calls luaD_protectedparser) - the
#       loader lives just outside, so dump wider.
#   [3] live registry cross-check (arrays fixed) - prints every table it
#       finds and the RVAs of a target-name list.
#   [4] NEW: find the runtime registry of the Unit*/Target*/RunScript APIs.
#       These names have NO static {name,fn} entry anywhere in .data, so
#       they are registered by code into a runtime structure. Scan the
#       module for qwords equal to the RELOCATED name-string pointers and
#       dump the neighbourhood - that structure holds their handler RVAs.
#   [5] unit inventory v2 (worked in hunt32 - keep as regression).
#   [6] lua_State + global_State dump (offline layout work for Phase 3).
#
# Run ELEVATED, in-game (logged in, NPC targeted):
#   powershell -ExecutionPolicy Bypass -File C:\ghidra-scripts\hunt33.ps1 | Tee-Object C:\ghidra-scripts\hunt33-out.txt
# Then upload BOTH hunt33-out.txt and C:\ghidra-scripts\hunt33-code.txt.
# No interaction required - unlike hunt32 there is nothing to press.
# =====================================================================
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct MBI33 {
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
public static class H33 {
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, out IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern ulong VirtualQueryEx(IntPtr h, IntPtr addr, out MBI33 mbi, ulong len);
  [DllImport("kernel32.dll", EntryPoint="VirtualProtectEx", SetLastError=true)]
  static extern bool VirtualProtectExRaw(IntPtr h, IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);
  public static bool ProtectEx(IntPtr h, IntPtr addr, int size, uint newProtect, out uint oldProtect) {
    return VirtualProtectExRaw(h, addr, (UIntPtr)(ulong)size, newProtect, out oldProtect);
  }
  public static bool Query(IntPtr h, ulong addr, out ulong rbase, out ulong rsize, out uint state, out uint prot) {
    MBI33 m;
    rbase = 0; rsize = 0; state = 0; prot = 0;
    if (VirtualQueryEx(h, (IntPtr)(long)addr, out m, 48) == 0) { return false; }
    rbase = m.BaseAddress; rsize = m.RegionSize; state = m.State; prot = m.Protect;
    return true;
  }
  // Scan a raw byte window for qwords equal to one of the needles; report offsets.
  public static int ScanQwords(byte[] hay, int hayLen, long[] needles, int nNeedles,
                               long[] outOffsets, int[] outWhich, int cap) {
    int count = 0;
    for (int i = 0; i + 8 <= hayLen; i += 8) {
      ulong q = BitConverter.ToUInt64(hay, i);
      for (int k = 0; k < nNeedles; k++) {
        if (q == (ulong)needles[k]) {
          if (count < cap) { outOffsets[count] = i; outWhich[count] = k; }
          count++;
          break;
        }
      }
    }
    return count;
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
# PROCESS_QUERY_INFORMATION(0x400) | PROCESS_VM_OPERATION(0x8) | PROCESS_VM_READ(0x10)
$hProc = [H33]::OpenProcess(0x0428, $false, $proc.Id)
if ($hProc -eq [IntPtr]::Zero) {
  Write-Output ("[HDR] OpenProcess(0x0428) FAILED: {0} - retrying with 0x0410 (flips will fail again)" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error())
  $hProc = [H33]::OpenProcess(0x0410, $false, $proc.Id)
}
if ($hProc -eq [IntPtr]::Zero) { throw ("OpenProcess failed: {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Write-Output ("[HDR] pid={0} base=0x{1:X} moduleSize=0x{2:X}" -f $proc.Id, $base, $modSize)

# ---------------------------------------------------------------- helpers
function Get-Bytes([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H33]::ReadProcessMemory($hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
  if ($got.ToInt64() -ne $n) { return $null }
  return , $buf
}
function Get-Qword([uint64]$addr) { $b = Get-Bytes $addr 8; if ($null -eq $b) { return [uint64]0 }; return [BitConverter]::ToUInt64($b, 0) }
function Get-Dword([uint64]$addr) { $b = Get-Bytes $addr 4; if ($null -eq $b) { return [uint32]0 }; return [BitConverter]::ToUInt32($b, 0) }
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
# FIX (hunt32 BUG 1 in the dump helper): never subtract before formatting.
# The hunt32 lines were built as "0x{0:X}" -f ($addr - $base): for a heap
# address (lua_State 0x15D...) the subtraction underflows to Decimal and
# Decimal has no "X" format -> "Format specifier was invalid" 8 times.
function RvaStrOf([uint64]$p) {
  if ($p -ge $base -and $p -lt ($base + $modSize)) { return ('0x{0:X}' -f ($p - $base)) }
  return ('ABS 0x{0:X}' -f $p)
}
function Get-Prot([uint64]$addr) {
  $rb = [uint64]0; $rs = [uint64]0; $st = [uint32]0; $pr = [uint32]0
  if (-not [H33]::Query($hProc, $addr, [ref]$rb, [ref]$rs, [ref]$st, [ref]$pr)) { return -1 }
  return [int]$pr
}
function Get-Region([uint64]$addr) {
  $rb = [uint64]0; $rs = [uint64]0; $st = [uint32]0; $pr = [uint32]0
  if (-not [H33]::Query($hProc, $addr, [ref]$rb, [ref]$rs, [ref]$st, [ref]$pr)) { return $null }
  return @{ base = $rb; size = $rs; state = $st; prot = $pr }
}
function Looks-LikeCode([byte[]]$b) {
  if ($null -eq $b -or $b.Length -lt 8) { return 'n/a' }
  # 0xCC padding anywhere in the first 64 bytes is a strong "this is code" hint,
  # encrypted bytes almost never contain a run of int3 fill.
  $cc = 0
  for ($i = 0; $i -lt [Math]::Min(64, $b.Length); $i++) { if ($b[$i] -eq 0xCC) { $cc++ } }
  $b0 = $b[0]
  $prologue = ($b0 -eq 0x40 -or $b0 -eq 0x48 -or $b0 -eq 0x4C -or $b0 -eq 0x53 -or $b0 -eq 0x55 -or $b0 -eq 0x56 -or $b0 -eq 0x57 -or $b0 -eq 0x41)
  $verdict = 'unclear'
  if ($cc -ge 4) { $verdict = 'CODE (int3 fill)' }
  elseif ($prologue) { $verdict = 'probably CODE (prologue byte 0x{0:X2})' -f $b0 }
  return $verdict
}

$codePath = 'C:\ghidra-scripts\hunt33-code.txt'
$script:CodeLines = New-Object System.Collections.ArrayList
function Code-Line([string]$s) { [void]$script:CodeLines.Add($s) }
function Code-DumpLive([uint64]$addr, [int]$size, [string]$tag) {
  Code-Line ("### LIVE {0} @ {1}" -f $tag, (RvaStrOf $addr))
  for ($off = 0; $off -lt $size; $off += 0x800) {
    $n = [Math]::Min(0x800, $size - $off)
    $b = Get-Bytes ($addr + [uint64]$off) $n
    if ($null -eq $b) { Code-Line ("MISS {0}" -f (RvaStrOf ($addr + [uint64]$off))); continue }
    for ($i = 0; $i -lt $n; $i += 32) {
      $s = ("{0} " -f (RvaStrOf ($addr + [uint64]$off + [uint64]$i)))
      $lim = [Math]::Min(32, $n - $i)
      for ($j = 0; $j -lt $lim; $j++) { $s += $b[$i + $j].ToString('X2') }
      Code-Line $s
    }
  }
}

# ---------------------------------------------------------------- packed file
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
$peOrig = $null
try { $peOrig = Open-PeFile $proc.MainModule.FileName } catch { }
if ($null -ne $peOrig) { Write-Output ("[FILE] shipped image: {0}" -f $peOrig.path) } else { Write-Output "[FILE] shipped image not readable" }

# ---------------------------------------------------------------- [0] sanity
Write-Output ""
Write-Output "== [0] sanity =="
$uL = Get-Qword ($base + 0x7B76AB8)
Write-Output ("[SAN] lua_State [base+0x7B76AB8] = 0x{0:X}" -f $uL)
Write-Output ("[SAN] player guid lo = 0x{0:X}  name='{1}'" -f (Get-Dword ($base + 0x61C72F0)), (Read-AsciiAt ($base + 0x61C7300) 32))
Write-Output ("[SAN] target guid lo = 0x{0:X}" -f (Get-Dword ($base + 0x7BDFF38)))

# ---------------------------------------------------------------- [1] the gate
Write-Output ""
Write-Output "== [1] cold-page inventory + FLIP TEST (OpenProcess now has PROCESS_VM_OPERATION) =="
$cand = @(
  @{ rva = 0x2C24040; n = 'CastSpellByName' },
  @{ rva = 0x2CC2DB0; n = 'RunMacro' },
  @{ rva = 0x2BBB7F0; n = 'FnEntityGetGuid' },
  @{ rva = 0x2C2B5E0; n = 'GetNumCompanions' },
  @{ rva = 0x2C27AD0; n = 'CastSpellByID' },
  @{ rva = 0x2CD5E60; n = 'GetActionInfo' },
  @{ rva = 0x2F8C9B0; n = 'AcceptQuest' },
  @{ rva = 0x23C9000; n = 'MoveForwardStop' },
  @{ rva = 0x23C76C0; n = 'ToggleRun' },
  @{ rva = 0x23EA630; n = 'RunBinding' },
  @{ rva = 0x1D98820; n = 'RegisterAll' },
  @{ rva = 0x1FC37F0; n = 'FnGuidToUnitChecked' },
  @{ rva = 0x25129E0; n = 'FnUnitNameFetch' },
  @{ rva = 0xAE96A0;  n = 'FnApiRegister' },
  @{ rva = 0x312CB70; n = 'misc-hot' },
  @{ rva = 0x471A960; n = 'lua_gettop' },
  @{ rva = 0x471B5C0; n = 'lua_pcall' },
  @{ rva = 0x4719A80; n = 'luaD_protectedparser' },
  @{ rva = 0x471B800; n = 'lua_pushcclosure' },
  @{ rva = 0x4719140; n = 'index2adr' },
  @{ rva = 0x471ECE0; n = 'luaD_pcall' },
  @{ rva = 0x471EA50; n = 'luaD_call' },
  @{ rva = 0x47190A0; n = 'f_call' },
  @{ rva = 0x471B5C0; n = 'lua_pcall(dup)' }
)
$flipOk = 0; $flipFail = 0; $alreadyRx = 0; $cold = 0
foreach ($row in $cand) {
  if ($row.n -like '*(dup)*') { continue }
  $a = $base + [uint64]$row.rva
  $reg = Get-Region $a
  if ($null -eq $reg) { Write-Output ("[FLIP] 0x{0:X} {1,-22} query failed" -f $row.rva, $row.n); continue }
  $prot = [int]$reg.prot
  $live = Get-Bytes $a 32
  $fileBytes = Get-FileBytesAt $peOrig ([uint64]$row.rva) 32
  $note = ''
  if ($null -eq $live) {
    $cold++
    if ($prot -eq 1) {
      $oldp = [uint32]0
      if ([H33]::ProtectEx($hProc, [IntPtr]([int64]$a), 0x1000, 0x20, [ref]$oldp)) {
        $live = Get-Bytes $a 32
        $flipOk++
        $note = ("FLIP OK (old=0x{0:X})" -f $oldp)
        $oldp2 = [uint32]0
        [void][H33]::ProtectEx($hProc, [IntPtr]([int64]$a), 0x1000, $oldp, [ref]$oldp2)
        $note += (" restored=0x{0:X}" -f [int](Get-Region $a).prot)
      } else {
        $flipFail++
        $note = ("FLIP FAILED err={0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error())
      }
    } else {
      $note = 'unreadable, not NOACCESS'
    }
  } elseif ($prot -eq 0x20 -or $prot -eq 0x40) {
    $alreadyRx++
    $note = 'already readable (warm page)'
  }
  Write-Output ("[FLIP] 0x{0:X} {1,-22} prot=0x{2:X} state=0x{3:X} regionSize=0x{4:X} {5}" -f $row.rva, $row.n, $prot, $reg.state, $reg.size, $note)
  Write-Output ("[FLIP]      live= {0}" -f (Hex-Of $live 16))
  Write-Output ("[FLIP]      file= {0}   file-verdict: {1}" -f (Hex-Of $fileBytes 16), (Looks-LikeCode $fileBytes))
  if ($null -ne $live) {
    Write-Output ("[FLIP]      live-verdict: {0}" -f (Looks-LikeCode $live))
  }
  $same = ''
  if ($null -ne $live -and $null -ne $fileBytes) {
    if ((Hex-Of $live 16) -eq (Hex-Of $fileBytes 16)) { $same = '  <<< live == PACKED FILE bytes' }
    else { $same = '  <<< live != packed file bytes' }
  }
  if ($same -ne '') { Write-Output ("[FLIP]     {0}" -f $same) }
}
Write-Output ("[FLIP] summary: cold={0} flipsOK={1} flipsFAILED={2} alreadyReadable={3}" -f $cold, $flipOk, $flipFail, $alreadyRx)
Write-Output "[FLIP] INTERPRETATION: if a cold page flipped OK and its live bytes differ from the"
Write-Output "[FLIP] packed file AND look like code -> the page is PLAINTEXT under the NOACCESS"
Write-Output "[FLIP] protection, and Executor64 may VirtualProtectEx + remote-call it directly."

# ---------------------------------------------------------------- [2] lua dump
Write-Output ""
Write-Output "== [2] wide Lua library dump 0x4700000..0x4740000 =="
Code-DumpLive ($base + 0x4700000) 0x40000 'lua-lib-wide'
Write-Output "[DUMP] 256 KB written to hunt33-code.txt"

# ---------------------------------------------------------------- [3] reg cross-check
Write-Output ""
Write-Output "== [3] live registry cross-check (uint64 arrays fixed) =="
$targetNames = @('CastSpellByName','CastSpellByID','UseAction','GetActionInfo','RunMacro','MoveForwardStart','JumpOrAscendStart','IsMouselooking','RunBinding','AcceptQuest','SpellStopCasting','GetName','ToggleRun','TurnLeftStart','SetMoveEnabled')
$strLo = $base + [uint64]0x4F00000
$strHi = $base + [uint64]0x6000000
$txtLo = $base + [uint64]0x1000
$txtHi = $base + [uint64]0x4F00000
$cap = 8192
$eRva = New-Object 'long[]' $cap
$eName = New-Object 'System.UInt64[]' $cap   # hunt32 BUG 2 fix
$eFn = New-Object 'System.UInt64[]' $cap
$found = New-Object System.Collections.ArrayList
$scanWins = @(
  @{ s = [uint64]0x5800000; len = 0x620000 }   # static .data raw region
)
foreach ($w in $scanWins) {
  Write-Output ("[SCAN] window base+0x{0:X} size=0x{1:X} (stride 16 - table entries are 16-aligned)" -f $w.s, $w.len)
  $chunk = 0x10000
  $done = 0
  for ($o = 0; $o -lt $w.len; $o += $chunk) {
    $b = Get-Bytes ($base + $w.s + [uint64]$o) $chunk
    $done += $chunk
    if (($done % 0x200000) -eq 0) { Write-Output ("[SCAN]   .. 0x{0:X}" -f $done) }
    if ($null -eq $b) { continue }
    $n = 0
    for ($i = 0; $i + 16 -le $b.Length; $i += 16) {
      $np = [BitConverter]::ToUInt64($b, $i)
      if ($np -lt $strLo -or $np -ge $strHi) { continue }
      $fp = [BitConverter]::ToUInt64($b, $i + 8)
      if ($fp -lt $txtLo -or $fp -ge $txtHi) { continue }
      if ($n -lt $cap) { $eRva[$n] = [int64]($w.s + [uint64]$o + [uint64]$i); $eName[$n] = $np; $eFn[$n] = $fp }
      $n++
    }
    for ($k = 0; $k -lt [Math]::Min($n, $cap); $k++) {
      [void]$found.Add(@{ rva = $eRva[$k]; np = $eName[$k]; fn = $eFn[$k] })
    }
  }
}
Write-Output ("[SCAN] candidate entries: {0}" -f $found.Count)
$tbl = New-Object System.Collections.ArrayList
foreach ($f in $found) {
  $nm = Read-AsciiAt $f.np 40
  if ($nm.Length -lt 3) { continue }
  [void]$tbl.Add([pscustomobject]@{ rva = [int64]$f.rva; n = $nm; fn = (RvaOf $f.fn) })
}
Write-Output ("[SCAN] named entries: {0}" -f $tbl.Count)
$sorted = $tbl | Sort-Object -Property rva
$groups = New-Object System.Collections.ArrayList
$cur = $null
foreach ($e in $sorted) {
  if ($null -ne $cur -and ($e.rva -eq ($cur.last + 16))) { [void]$cur.items.Add($e); $cur.last = $e.rva }
  else {
    $cur = @{ base = $e.rva; last = $e.rva; items = (New-Object System.Collections.ArrayList) }
    [void]$cur.items.Add($e); [void]$groups.Add($cur)
  }
}
$groups = $groups | Where-Object { $_.items.Count -ge 2 }
Write-Output ("[SCAN] tables: {0}" -f $groups.Count)
foreach ($g in $groups) {
  Write-Output ("[TBL] base=0x{0:X} entries={1} first={2} last={3}" -f $g.base, $g.items.Count, $g.items[0].n, $g.items[$g.items.Count - 1].n)
}
foreach ($nm in $targetNames) {
  $hit = $null
  foreach ($e in $tbl) { if ($e.n -eq $nm) { $hit = $e; break } }
  if ($null -ne $hit) { Write-Output ("[API] {0,-20} -> 0x{1:X}" -f $nm, $hit.fn) }
  else { Write-Output ("[API] {0,-20} -> not in a static table (runtime-registered)" -f $nm) }
}

# ---------------------------------------------------------------- [4] runtime registry hunt
Write-Output ""
Write-Output "== [4] runtime registry of the runtime-registered APIs (Unit*/Target*/RunScript) =="
# Name-string RVAs measured in the shipped image (rdata is NOT packed):
#   RunScript  0x4F45FA8 | TargetUnit 0x4F8BDF0 | UnitName 0x4F8ED08
#   UnitHealth 0x4FA0F68 | UnitExists 0x4F9FDE8 | + control CastSpellByName 0x5120A70
$needleInfo = @(
  @{ rva = 0x4F45FA8; n = 'RunScript' },
  @{ rva = 0x512BA68; n = 'RunMacro' },
  @{ rva = 0x4F8BDF0; n = 'TargetUnit' },
  @{ rva = 0x5099B60; n = 'TargetUnit.b' },
  @{ rva = 0x4F8ED08; n = 'UnitName' },
  @{ rva = 0x511ED02; n = 'UnitName.b' },
  @{ rva = 0x4FA0F68; n = 'UnitHealth' },
  @{ rva = 0x4F9FDE8; n = 'UnitExists' },
  @{ rva = 0x4F8EB70; n = 'UnitLevel' },
  @{ rva = 0x4F44A00; n = 'UnitGUID' },
  @{ rva = 0x4F8BBB0; n = 'TargetNearestEnemy' },
  @{ rva = 0x4F8B610; n = 'AttackTarget' },
  @{ rva = 0x4F9FD60; n = 'UnitAffectingCombat' },
  @{ rva = 0x5120A70; n = 'CastSpellByName(CONTROL)' },
  @{ rva = 0x512C068; n = 'UseAction(CONTROL)' }
)
$needles = New-Object 'long[]' ($needleInfo.Count)
for ($k = 0; $k -lt $needleInfo.Count; $k++) { $needles[$k] = [int64]($base + [uint64]$needleInfo[$k].rva) }
$scanRanges = @(
  @{ s = [uint64]0x5800000; len = 0x2A10000 }   # whole .data: raw part + bss, to 0x8210000
  @{ s = [uint64]0x8210000; len = 0x200000 }    # behind the image (hunt29 saw tables here)
)
$capN = 512
$hitOff = New-Object 'long[]' $capN
$hitWhich = New-Object 'int[]' $capN
$total = 0
$dumpedHits = 0
foreach ($rg in $scanRanges) {
  Write-Output ("[REGSCAN] base+0x{0:X} .. +0x{1:X}" -f $rg.s, ($rg.s + $rg.len))
  $chunk = 0x10000
  for ($o = 0; $o -lt $rg.len; $o += $chunk) {
    $b = Get-Bytes ($base + $rg.s + [uint64]$o) $chunk
    if ($null -eq $b) { continue }
    $n = [H33]::ScanQwords($b, $b.Length, $needles, $needleInfo.Count, $hitOff, $hitWhich, $capN)
    if ($n -gt 0) { $total += $n }
    for ($k = 0; $k -lt [Math]::Min($n, $capN); $k++) {
      $at = $base + $rg.s + [uint64]$o + [uint64]$hitOff[$k]
      Write-Output ("[REGSCAN] HIT {0,-26} at base+0x{1:X}" -f $needleInfo[$hitWhich[$k]].n, (RvaOf $at))
      if ($dumpedHits -lt 12) {
        Code-DumpLive ($at - [uint64]0x80) 0x300 ("registry-near-" + $needleInfo[$hitWhich[$k]].n)
        $dumpedHits++
      }
    }
  }
}
Write-Output ("[REGSCAN] total hits: {0}" -f $total)
if ($total -eq 0) {
  Write-Output "[REGSCAN] no relocated name pointers found in the module - the registry"
  Write-Output "[REGSCAN] lives on the heap; the Lua-globals walk is then the way in."
}

# ---------------------------------------------------------------- [5] units
Write-Output ""
Write-Output "== [5] unit inventory v2 (regression) =="
$poolTable = $base + 0x582D150
$kinds = @{}
$goodUnits = 0; $badUnits = 0; $occTotal = 0
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
    $isGood = ($kind -eq 'PLAYER' -or $kind -eq 'NPC') -and $back -and $g10 -and $g18
    if ($isGood) {
      $goodUnits++
      Write-Output ("[UNIT] f={0} slot={1,3} kind={2,-5} lvl={3,3} hp={4}/{5} pow={6} flags=0x{7:X}" -f `
        $f, $k, $kind, (Get-Dword ($unit + 0x1E40)), (Get-Dword ($unit + 0x1E30)), (Get-Dword ($unit + 0x1E38)), (Get-Dword ($unit + 0x1BC8)), (Get-Dword ($unit + 0x1E60)))
    } else { $badUnits++ }
  }
}
Write-Output ("[UNIT] occupied={0} realUnits={1} rejected={2}" -f $occTotal, $goodUnits, $badUnits)

# ---------------------------------------------------------------- [6] lua state dump
Write-Output ""
Write-Output "== [6] lua_State + global_State dump =="
if (Test-Pointer $uL) {
  Code-DumpLive $uL 0x140 'lua_State'
  $G = Get-Qword ($uL + 0x38)
  Write-Output ("[LUA] L=0x{0:X}  top=0x{1:X} base=0x{2:X} G=0x{3:X} ci=0x{4:X} stack=0x{5:X}" -f `
    $uL, (Get-Qword ($uL + 0x28)), (Get-Qword ($uL + 0x30)), $G, (Get-Qword ($uL + 0x40)), (Get-Qword ($uL + 0x58)))
  if (Test-Pointer $G) {
    Code-DumpLive $G 0x200 'global_State'
    Write-Output ("[LUA] G totalbytes(G+0x70)={0} threshold(G+0x78)={1}" -f (Get-Qword ($G + 0x70)), (Get-Qword ($G + 0x78)))
  }
  # stack snapshot: the 24-byte TValue layout means (top-base)/24 values
  $top = Get-Qword ($uL + 0x28); $bas = Get-Qword ($uL + 0x30)
  if ((Test-Pointer $top) -and (Test-Pointer $bas) -and ($top -ge $bas) -and (($top - $bas) -lt 0x2000)) {
    $nvals = [int](($top - $bas) / 24)
    Write-Output ("[LUA] current frame has {0} stack slots (stride 24)" -f $nvals)
    for ($i = 0; $i -lt [Math]::Min($nvals, 12); $i++) {
      $slot = $bas + [uint64]($i * 24)
      Write-Output ("[LUA]  slot[{0}] value=0x{1:X16} tt={2} extra=0x{3:X16}" -f $i, (Get-Qword $slot), (Get-Dword ($slot + 8)), (Get-Qword ($slot + 0x10)))
    }
  } else {
    Write-Output ("[LUA] top/base not sane: top=0x{0:X} base=0x{1:X}" -f $top, $bas)
  }
} else { Write-Output "[LUA] lua_State global is null" }

# ---------------------------------------------------------------- write code file
try {
  $sw = New-Object System.IO.StreamWriter($codePath, $false, [Text.Encoding]::ASCII)
  foreach ($l in $script:CodeLines) { $sw.WriteLine($l) }
  $sw.Close()
  Write-Output ("[DUMP] {0} lines written to {1}" -f $script:CodeLines.Count, $codePath)
} catch {
  Write-Output ("[DUMP] could not write {0}: {1}" -f $codePath, $_.Exception.Message)
}
if ($null -ne $peOrig) { try { $peOrig.fs.Close() } catch { } }
[void][H33]::CloseHandle($hProc)
Write-Output "[DONE]"
