# hunt34.ps1 - Phase-3 bridge validation for WowB (x64, 1.60.1 build 70291)
# ---------------------------------------------------------------------------
# hunt33's negatives were a rights bug: OpenProcess(0x0428) has VM_WRITE but NOT
# VM_READ -> every ReadProcessMemory failed. This script uses 0x1F0FFF.
#
# [1] cold-page verdict: flip a NOACCESS page to RX, read it, restore. The bytes
#     either equal the SHIPPED FILE bytes (=> page is still CIPHERTEXT: the
#     packer decrypts per-page only when the game itself executes it) or the
#     DECODED dump bytes (=> plaintext under a pure access guard).
# [2] Lua bridge proof: remote thread does the game's own registration recipe
#        lua_pushcclosure(L, handlerRva, 0)
#        lua_pushlstring (L, "player", 6)
#        lua_pcall       (L, 1, 1, 0)
#     three times: (a) lua_gettop  (machinery smoke test, always-warm fn)
#                  (b) UnitHealth (0x1D63190)  -> expect tt=3 number
#                  (c) UnitName   (0x1D7B840)  -> expect a string, read via
#                                                  lua_tolstring(L,-1,NULL)
#     The handler pages are usually COLD, so (b)/(c) also test whether the
#     packer's on-execute decrypt handles a foreign thread (that is the key
#     question for driving arbitrary FrameScript APIs from outside).
#
# RUN: elevated PowerShell, WowB.exe running, character in world, idle.
# LOG: C:\ghidra-scripts\hunt34-out.txt
# ---------------------------------------------------------------------------
param([switch]$ReadOnly)

$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt34-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H34 {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr wrote);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFreeEx(IntPtr h, IntPtr a, IntPtr sz, uint f);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr a, IntPtr sz, uint np, out uint old);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr sa, IntPtr st, IntPtr fn, IntPtr par, uint fl, out uint tid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetExitCodeThread(IntPtr h, out uint code);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
}
'@

# ---------------------------------------------------------------- helpers
$script:hProc = [IntPtr]::Zero
$script:base  = [uint64]0

function RPM([uint64]$addr, [int]$n) {
  $buf = New-Object byte[] $n
  $got = [IntPtr]::Zero
  if (-not [H34]::ReadProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
  if ($got.ToInt64() -lt $n) { return $null }
  return , $buf
}
function U64At([uint64]$addr) {
  $b = RPM $addr 8
  if ($null -eq $b) { return [uint64]0 }
  return [System.BitConverter]::ToUInt64($b, 0)
}
function HexOf([byte[]]$b, [int]$n) {
  if ($null -eq $b) { return 'none' }
  $k = [Math]::Min($n, $b.Length)
  $s = ''
  for ($i = 0; $i -lt $k; $i++) { $s += ('{0:x2}' -f $b[$i]) }
  return $s
}
function U64Bytes([uint64]$v) { return , [System.BitConverter]::GetBytes($v) }
function HexToBytes([string]$h) {
  $n = [int]($h.Length / 2)
  $b = New-Object byte[] $n
  for ($i = 0; $i -lt $n; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i * 2, 2), 16) }
  return , $b
}
function PatchAll([byte[]]$buf, [byte[]]$find, [byte[]]$repl) {
  for ($i = 0; $i -le $buf.Length - $find.Length; $i++) {
    $ok = $true
    for ($j = 0; $j -lt $find.Length; $j++) { if ($buf[$i + $j] -ne $find[$j]) { $ok = $false; break } }
    if ($ok) { for ($j = 0; $j -lt $repl.Length; $j++) { $buf[$i + $j] = $repl[$j] } }
  }
  return , $buf
}

# ---------------------------------------------------------------- attach
Say "[HDR] hunt34 - Phase-3 bridge validation"
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the game and rerun'; exit 1 }
$script:hProc = [H34]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H34]::OpenProcess(0x043A, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H34]::OpenProcess(0x0418, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { Say ('[HDR] OpenProcess failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 1 }
$script:base = [uint64]$proc.MainModule.BaseAddress.ToInt64()
$modSize = [uint64]$proc.MainModule.ModuleMemorySize
Say ('[HDR] pid=' + $proc.Id + ' base=0x' + ('{0:X}' -f $script:base) + ' moduleSize=0x' + ('{0:X}' -f $modSize))

$L = U64At ($script:base + [uint64]0x7B76AB8)
Say ('[SAN] lua_State [base+0x7B76AB8] = 0x' + ('{0:X}' -f $L))

# warm-page read sanity (page 0x471B000 must be decrypted after any Lua use)
$warm = RPM ($script:base + [uint64]0x471B5C0) 16
Say ('[SAN] warm page 0x471B5C0 lua_pcall live= ' + (HexOf $warm 16))
$expWarm = '4053574883ec484c8b51288d4201'
if ($null -ne $warm -and (HexOf $warm 16) -eq $expWarm) {
  Say '[SAN] matches the decoded dump - ReadProcessMemory works, Lua pages are WARM'
} elseif ($null -eq $warm) {
  Say '[SAN] WARNING: could not read a page that should be warm - rights or state problem'
} else {
  Say '[SAN] NOTE: live bytes differ from the known decoded bytes (page cold or game state odd)'
}

# ---------------------------------------------------------------- [1] cold pages
Say ''
Say '== [1] cold-page verdict (flip NOACCESS -> RX, read, restore) =='
$probes = @(
  @{ rva = 0x2C24040; n = 'CastSpellByName';  pg = 0x2C24000; cipher = '338d106612e1da54c945ce0657ff315c'; plain = '4055535657415541564157488dac2450' },
  @{ rva = 0x2CC2DB0; n = 'RunMacro';         pg = 0x2CC2000; cipher = '2b520f115fa6c54e3e7b37a8b95f3164'; plain = '48895c2408574883ec20488bd9e83efe' },
  @{ rva = 0x2BBB7F0; n = 'FnEntityGetGuid';  pg = 0x2BBB000; cipher = '8c1b1d38524b8b6e4dfc7f6b46102b45'; plain = '48895c2418574883ec20488bfa488bd9' },
  @{ rva = 0x2C2B5E0; n = 'GetNumCompanions'; pg = 0x2C2B000; cipher = 'a443b184f1c33985d793e20544346973'; plain = '48895c240848896c2410488974241848' }
)
foreach ($p in $probes) {
  $va = $script:base + [uint64]$p.rva
  $live = RPM $va 16
  if ($null -ne $live) {
    Say ('[FLIP] 0x' + ('{0:X}' -f $p.rva) + ' ' + $p.n + ' ALREADY READABLE live= ' + (HexOf $live 16))
  } else {
    $old = [uint32]0
    $ok = [H34]::VirtualProtectEx($script:hProc, [IntPtr]([int64]($script:base + [uint64]$p.pg)), [IntPtr]4096, 0x20, [ref]$old)
    if (-not $ok) {
      Say ('[FLIP] 0x' + ('{0:X}' -f $p.rva) + ' ' + $p.n + ' flip FAILED err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    } else {
      $live = RPM $va 16
      $h = HexOf $live 16
      $old2 = [uint32]0
      [void][H34]::VirtualProtectEx($script:hProc, [IntPtr]([int64]($script:base + [uint64]$p.pg)), [IntPtr]4096, [uint32]$old, [ref]$old2)
      $verdict = 'OTHER'
      if ($null -eq $live) { $verdict = 'STILL UNREADABLE' }
      elseif ($h -eq $p.cipher) { $verdict = 'CIPHERTEXT (unchanged packed bytes - packer decrypts only when the game executes it)' }
      elseif ($h -eq $p.plain) { $verdict = 'PLAINTEXT (decoded code under a pure access guard)' }
      Say ('[FLIP] 0x' + ('{0:X}' -f $p.rva) + ' ' + $p.n + ' oldProt=0x' + ('{0:X}' -f $old) + ' live= ' + $h)
      Say ('[FLIP]        verdict: ' + $verdict + '   (restored to 0x' + ('{0:X}' -f $old) + ')')
    }
  }
}

if ($ReadOnly) {
  Say ''
  Say '[DONE] -ReadOnly: Lua bridge skipped.'
  Say ('[DONE] log: ' + $logPath)
  exit 0
}

# ---------------------------------------------------------------- [2] Lua bridge
Say ''
Say '== [2] Lua bridge: pushcclosure + pushlstring + pcall =='
if ($L -eq 0) { Say '[LUA] lua_State is null - log in / enter world, then rerun'; exit 0 }

$page = [H34]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
if ($page -eq [IntPtr]::Zero) { Say ('[LUA] VirtualAllocEx failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 1 }
$pageVA = [uint64]$page.ToInt64()
Say ('[LUA] stub page at 0x' + ('{0:X}' -f $pageVA))

# machine code template (479 bytes; offsets pinned, markers patched below)
$stubHex = '4883ec2848bbaaaaaaaaaaaaaaaa48b81111111111111111488b40284889433848b9111111111111111148ba22222222222222224533c048b85555555555555555ffd048b91111111111111111ba0000000041b8010000004533c948b87777777777777777ffd089430048b81111111111111111488b40280fb748f066894b18488b50e84889531048b9111111111111111148ba33333333333333334533c048b85555555555555555ffd048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd089430448b81111111111111111488b40280fb748f066894b28488b50e84889532048b9111111111111111148ba44444444444444444533c048b85555555555555555ffd048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd089430848b91111111111111111baffffffff4533c048b88888888888888888ffd04889433048b81111111111111111488b40284889434048b81111111111111111488b53384889502848b88877665544332211488943484883c428c3'
$code = HexToBytes $stubHex
Say ('[LUA] stub template bytes = ' + $code.Length)

$code = PatchAll $code (U64Bytes 0x1111111111111111) (U64Bytes $L)                                  # lua_State
$code = PatchAll $code (U64Bytes 0x2222222222222222) (U64Bytes ($script:base + [uint64]0x471A960))  # lua_gettop
$code = PatchAll $code (U64Bytes 0x3333333333333333) (U64Bytes ($script:base + [uint64]0x1D63190))  # UnitHealth
$code = PatchAll $code (U64Bytes 0x4444444444444444) (U64Bytes ($script:base + [uint64]0x1D7B840))  # UnitName
$code = PatchAll $code (U64Bytes 0x5555555555555555) (U64Bytes ($script:base + [uint64]0x471B800))  # pushcclosure
$code = PatchAll $code (U64Bytes 0x6666666666666666) (U64Bytes ($script:base + [uint64]0x471BC20))  # pushlstring
$code = PatchAll $code (U64Bytes 0x7777777777777777) (U64Bytes ($script:base + [uint64]0x471B5C0))  # pcall
$code = PatchAll $code (U64Bytes 0x8888888888888888) (U64Bytes ($script:base + [uint64]0x471B4F0))  # tolstring
$code = PatchAll $code (U64Bytes 0x9999999999999999) (U64Bytes ($pageVA + [uint64]0x200))           # "player"
$code = PatchAll $code (U64Bytes 0xAAAAAAAAAAAAAAAA) (U64Bytes $pageVA)                             # scratch base

# write "player" at page+0x200 and the stub at page+0x300
$strBytes = New-Object byte[] 16
$strBytes[0] = [byte][char]'p'; $strBytes[1] = [byte][char]'l'; $strBytes[2] = [byte][char]'a'
$strBytes[3] = [byte][char]'y'; $strBytes[4] = [byte][char]'e'; $strBytes[5] = [byte][char]'r'
$wrote = [IntPtr]::Zero
if (-not [H34]::WriteProcessMemory($script:hProc, [IntPtr]([int64]($pageVA + [uint64]0x200)), $strBytes, 16, [ref]$wrote)) {
  Say ('[LUA] WPM(string) failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 1
}
if (-not [H34]::WriteProcessMemory($script:hProc, [IntPtr]([int64]($pageVA + [uint64]0x300)), $code, $code.Length, [ref]$wrote)) {
  Say ('[LUA] WPM(stub) failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()); exit 1
}
Say '[LUA] stub written; starting remote thread...'

$tid = [uint32]0
$th = [H34]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($pageVA + [uint64]0x300)), [IntPtr]::Zero, 0, [ref]$tid)
if ($th -eq [IntPtr]::Zero) {
  Say ('[LUA] CreateRemoteThread failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
} else {
  $wait = [H34]::WaitForSingleObject($th, 20000)
  $exitCode = [uint32]0
  [void][H34]::GetExitCodeThread($th, [ref]$exitCode)
  Say ('[LUA] thread tid=' + $tid + ' wait=' + $wait + ' exitCode=0x' + ('{0:X}' -f $exitCode) + ' (expect 0x55667788)')
  [void][H34]::CloseHandle($th)

  $sc = RPM $pageVA 0x50
  if ($null -eq $sc) {
    Say '[LUA] could not read the scratch block - process likely crashed inside the remote thread'
  } else {
    $st1 = [System.BitConverter]::ToInt32($sc, 0x00)
    $st2 = [System.BitConverter]::ToInt32($sc, 0x04)
    $st3 = [System.BitConverter]::ToInt32($sc, 0x08)
    $v1 = [System.BitConverter]::ToDouble($sc, 0x10)
    $t1 = [System.BitConverter]::ToUInt16($sc, 0x18)
    $v2 = [System.BitConverter]::ToDouble($sc, 0x20)
    $t2 = [System.BitConverter]::ToUInt16($sc, 0x28)
    $sptr = [System.BitConverter]::ToUInt64($sc, 0x30)
    $tope = [System.BitConverter]::ToUInt64($sc, 0x40)
    $done = [System.BitConverter]::ToUInt64($sc, 0x48)
    Say ('[LUA] done marker = 0x' + ('{0:X}' -f $done) + '   (0x1122334455667788 = stub ran to the end)')
    Say ('[LUA] (a) machinery  pcallStatus=' + $st1 + '  result tt=' + $t1 + ' value=' + $v1)
    Say ('[LUA] (b) UnitHealth pcallStatus=' + $st2 + '  result tt=' + $t2 + ' value=' + $v2)
    Say ('[LUA] (c) UnitName   pcallStatus=' + $st3 + '  tolstring char*=0x' + ('{0:X}' -f $sptr))
    if ($sptr -ne 0) {
      $nm = RPM $sptr 64
      if ($null -ne $nm) {
        $end = 0
        while ($end -lt $nm.Length -and $nm[$end] -ne 0) { $end++ }
        $txt = [System.Text.Encoding]::ASCII.GetString($nm, 0, $end)
        Say ('[LUA] UnitName("player") = "' + $txt + '"')
      } else {
        Say '[LUA] could not read the name string pointer'
      }
    }
    Say ('[LUA] lua top after stub restore: 0x' + ('{0:X}' -f $tope))
    Say ''
    if ($st1 -eq 0 -and $t1 -eq 3 -and $done -eq 0x1122334455667788) {
      Say '[LUA] VERDICT (a): bridge machinery WORKS (pushcclosure + pcall + result readback)'
    } else {
      Say '[LUA] VERDICT (a): machinery did not produce the expected number - check statuses above'
    }
    if ($st2 -eq 0 -and $t2 -eq 3) {
      Say ('[LUA] VERDICT (b): UnitHealth("player") = ' + $v2 + ' -> cold-page handler executed fine')
    } else {
      Say '[LUA] VERDICT (b): UnitHealth did not return a number (status ' + $st2 + ')'
    }
  }
}

[void][H34]::VirtualFreeEx($script:hProc, $page, [IntPtr]::Zero, 0x8000)
[void][H34]::CloseHandle($script:hProc)
Say ''
Say '[DONE] upload C:\ghidra-scripts\hunt34-out.txt'
