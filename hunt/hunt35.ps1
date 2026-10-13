# hunt35.ps1 - why did hunt34's remote thread execute nothing, and is there a
#              working "run our code inside WowB" vector?
# ---------------------------------------------------------------------------
# hunt34 facts: rights are fine (0x1F0FFF), lua_State read OK, lua_pcall page is
# warm and byte-identical to the decoded dump (the "[SAN] NOTE" was my own
# 13-vs-16 byte compare bug, ignore it).  But its remote thread terminated with
# exit code 0x0 having written NOTHING into its scratch block: either it never
# executed one instruction (anti-tamper neutering thread starts), or it started
# and died on an early fault.  [1] below distinguishes those two with an
# instrumented stub that stamps a STAGE counter (0x1001..0x100B) after every step.
#
# [1] remote thread + instrumented payload (lua_pushnil smoke test, then the real
#     pushcclosure/pushlstring/pcall bridge for UnitHealth and UnitName)
# [2] same payload on a *hijacked existing* game thread (SuspendThread ->
#     SetThreadContext(Rip=stub) -> ResumeThread -> restore): no thread creation,
#     so a "kill foreign threads at start" hook cannot see it.  Runs only when [1]
#     does not complete.
# [3] cold-page question: if some execution vector works, call one handler whose
#     page is still NOACCESS WITHOUT touching its protection.  If that works, the
#     packer decrypts on fault (process-wide) and every handler is reachable; if
#     the thread dies there, cold pages need the game itself to execute them.
# [4] cleanup (the stub page is only freed when no thread can still be inside it)
#
# EXPECTED RISK - read before running:
#   [2] borrows ONE idle game thread for a few milliseconds (its context is
#       saved and restored).  If the client freezes, close and relaunch it.
#   [3] calls a handler whose page is still encrypted.  If nothing inside the
#       client handles a fault there, the client dies - and that IS the answer
#       to the cold-page question, so run it where a crash is tolerable.
#       -SkipHijack skips [2], -SkipCold skips [3].
#
# RUN: elevated PowerShell, WowB.exe running, character in world, IDLE (do not
#      move/click - a hijacked thread is borrowed for a few milliseconds).
# LOG: C:\ghidra-scripts\hunt35-out.txt
# ---------------------------------------------------------------------------
param([switch]$SkipHijack, [switch]$SkipCold)

$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt35-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function GetLastErr { return [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H35 {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr wrote);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFreeEx(IntPtr h, IntPtr a, IntPtr sz, uint f);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern ulong VirtualQueryEx(IntPtr h, IntPtr a, byte[] buf, ulong len);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr sa, IntPtr st, IntPtr fn, IntPtr par, uint fl, out uint tid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetExitCodeThread(IntPtr h, out uint code);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern uint SuspendThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern uint ResumeThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetThreadContext(IntPtr h, IntPtr ctx);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAlloc(IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFree(IntPtr a, IntPtr sz, uint f);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetProcessMitigationPolicy(IntPtr h, int policy, byte[] buf, IntPtr len);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
}
[StructLayout(LayoutKind.Sequential)]
public struct THREADENTRY32 {
  public uint dwSize; public uint cntUsage; public uint th32ThreadID; public uint th32OwnerProcessID;
  public int tpBasePri; public int tpDeltaPri; public uint dwFlags;
}
'@

# ---------------------------------------------------------------- helpers
$script:hProc   = [IntPtr]::Zero
$script:base    = [uint64]0
$script:pageVA  = [uint64]0
$script:gamePid = 0
$script:mapVerdict = @{}
$script:mapRva     = @{}

function NewBytes([int]$n) { return [byte[]]::new($n) }
function U64Bytes([uint64]$v) { return , [System.BitConverter]::GetBytes($v) }
function HexOf([byte[]]$b, [int]$n) {
  if ($null -eq $b) { return 'none' }
  $k = [Math]::Min($n, $b.Length)
  $s = ''
  for ($i = 0; $i -lt $k; $i++) { $s += ('{0:x2}' -f $b[$i]) }
  return $s
}
function HexToBytes([string]$h) {
  $n = [int]($h.Length / 2)
  $b = NewBytes $n
  for ($i = 0; $i -lt $n; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i * 2, 2), 16) }
  return , $b
}
function U16B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToUInt16($b, $o) }
function I32B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToInt32($b, $o) }
function U64B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToUInt64($b, $o) }
function F64B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToDouble($b, $o) }
function RPM([uint64]$addr, [int]$n) {
  if ($addr -eq 0) { return $null }
  $buf = NewBytes $n
  $got = [IntPtr]::Zero
  if (-not [H35]::ReadProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
  if ($got.ToInt64() -lt $n) { return $null }
  return , $buf
}
function U64A([uint64]$addr) {
  $b = RPM $addr 8
  if ($null -eq $b) { return [uint64]0 }
  return [System.BitConverter]::ToUInt64($b, 0)
}
function WPM([uint64]$addr, [byte[]]$buf) {
  $wrote = [IntPtr]::Zero
  if (-not [H35]::WriteProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $buf.Length, [ref]$wrote)) { return $false }
  return ($wrote.ToInt64() -eq $buf.Length)
}
function PatchAll([byte[]]$buf, [byte[]]$find, [byte[]]$repl) {
  for ($i = 0; $i -le $buf.Length - $find.Length; $i++) {
    $ok = $true
    for ($j = 0; $j -lt $find.Length; $j++) { if ($buf[$i + $j] -ne $find[$j]) { $ok = $false; break } }
    if ($ok) { for ($j = 0; $j -lt $repl.Length; $j++) { $buf[$i + $j] = $repl[$j] } }
  }
  return , $buf
}
function PatchStub([string]$hex, [uint64]$handlerRva) {
  $c = HexToBytes $hex
  $c = PatchAll $c (U64Bytes 0x1111111111111111) (U64Bytes $script:L)
  $c = PatchAll $c (U64Bytes 0xAAAAAAAAAAAAAAAA) (U64Bytes $script:pageVA)
  $c = PatchAll $c (U64Bytes 0x3333333333333333) (U64Bytes ($script:base + [uint64]0x1D63190))  # UnitHealth
  $c = PatchAll $c (U64Bytes 0x4444444444444444) (U64Bytes ($script:base + [uint64]0x1D7B840))  # UnitName
  $c = PatchAll $c (U64Bytes 0x5555555555555555) (U64Bytes ($script:base + [uint64]0x471B800))  # pushcclosure
  $c = PatchAll $c (U64Bytes 0x6666666666666666) (U64Bytes ($script:base + [uint64]0x471BC20))  # pushlstring
  $c = PatchAll $c (U64Bytes 0x7777777777777777) (U64Bytes ($script:base + [uint64]0x471B5C0))  # lua_pcall
  $c = PatchAll $c (U64Bytes 0x8888888888888888) (U64Bytes ($script:base + [uint64]0x471B4F0))  # lua_tolstring
  $c = PatchAll $c (U64Bytes 0xBBBBBBBBBBBBBBBB) (U64Bytes ($script:base + [uint64]0x471BD00))  # lua_pushnil
  $c = PatchAll $c (U64Bytes 0x9999999999999999) (U64Bytes ($script:pageVA + [uint64]0x200))    # "player"
  if ($handlerRva -ne 0) {
    $c = PatchAll $c (U64Bytes 0xEEEEEEEEEEEEEEEE) (U64Bytes ($script:base + $handlerRva))       # cold handler
  }
  return , $c
}
function StageName([uint64]$s) {
  switch ($s) {
    0x1001 { return 'entered - first instructions executed' }
    0x1002 { return 'L->top read' }
    0x1003 { return 'lua_pushnil returned' }
    0x1004 { return 'pre lua_pushcclosure' }
    0x1005 { return 'lua_pushcclosure returned' }
    0x1006 { return 'lua_pushlstring returned' }
    0x1007 { return 'lua_pcall returned' }
    0x1008 { return 'result captured' }
    0x1009 { return 'lua_pcall #2 returned' }
    0x100A { return 'lua_tolstring returned' }
    0x100B { return 'COMPLETE - stub ran to the end' }
    default { return '(no stage stamp - nothing executed)' }
  }
}
function ShowScratch([byte[]]$sc, [string]$tag) {
  $stage = U64B $sc 0x50
  Say ('[' + $tag + '] STAGE=0x' + ('{0:X}' -f $stage) + '  -> ' + (StageName $stage))
  Say ('[' + $tag + '] done=0x' + ('{0:X}' -f (U64B $sc 0x48)) + '  top_before=0x' + ('{0:X}' -f (U64B $sc 0x38)) + '  top_after=0x' + ('{0:X}' -f (U64B $sc 0x40)))
  Say ('[' + $tag + '] (a) pushnil       status=' + (I32B $sc 0x00) + ' tt=' + (U16B $sc 0x18) + ' value=' + (F64B $sc 0x10))
  Say ('[' + $tag + '] (b) UnitHealth    status=' + (I32B $sc 0x04) + ' tt=' + (U16B $sc 0x28) + ' value=' + (F64B $sc 0x20))
  Say ('[' + $tag + '] (c) UnitName      status=' + (I32B $sc 0x08) + ' tolstring=0x' + ('{0:X}' -f (U64B $sc 0x30)))
  Say ('[' + $tag + '] (d) COLD handler  status=' + (I32B $sc 0x08) + ' tt=' + (U16B $sc 0x60) + ' value=' + (F64B $sc 0x58))
}
function ReadCStr([uint64]$addr, [int]$max) {
  if ($addr -lt 0x10000 -or $addr -gt 0x00007FFFFFFF0000) { return $null }
  $b = RPM $addr $max
  if ($null -eq $b) { return $null }
  $end = 0
  while ($end -lt $max -and $b[$end] -ne 0) { $end++ }
  return [System.Text.Encoding]::ASCII.GetString($b, 0, $end)
}
function AllocCtx {
  # x64 CONTEXT is 1232 bytes and must be 16-byte aligned: use a page-aligned
  # VirtualAlloc buffer instead of a byte[] (the byte[] path silently fails).
  $p = [H35]::VirtualAlloc([IntPtr]::Zero, [IntPtr]1232, 0x3000, 0x04)
  if ($p -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
  [Runtime.InteropServices.Marshal]::WriteInt32($p, 0x30, 0x10000B)   # CONTEXT_FULL
  return $p
}
function CtxRip([IntPtr]$p) { return [uint64][Runtime.InteropServices.Marshal]::ReadInt64($p, 0xF8) }
function CtxRsp([IntPtr]$p) { return [uint64][Runtime.InteropServices.Marshal]::ReadInt64($p, 0x98) }
function CtxSetRip([IntPtr]$p, [uint64]$v) { [Runtime.InteropServices.Marshal]::WriteInt64($p, 0xF8, [int64]$v) }
function ReadProt([uint64]$addr) {
  $mbi = NewBytes 0x30
  $r = [H35]::VirtualQueryEx($script:hProc, [IntPtr]([int64]$addr), $mbi, [uint64]0x30)
  if ($r -eq 0) { return @{ prot = -1; state = -1 } }
  return @{ prot = [System.BitConverter]::ToUInt32($mbi, 0x24); state = [System.BitConverter]::ToUInt32($mbi, 0x20) }
}

# ---------------------------------------------------------------- attach + sanity
Say '[HDR] hunt35 - execution-vector bring-up + cold-page test'
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the game and rerun'; exit 1 }
$script:gamePid = $proc.Id
$script:hProc = [H35]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H35]::OpenProcess(0x043A, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H35]::OpenProcess(0x0418, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { Say ('[HDR] OpenProcess failed err=' + (GetLastErr)); exit 1 }
$script:base = [uint64]$proc.MainModule.BaseAddress.ToInt64()
$script:L = U64A ($script:base + [uint64]0x7B76AB8)
Say ('[HDR] pid=' + $proc.Id + ' base=0x' + ('{0:X}' -f $script:base) + ' moduleSize=0x' + ('{0:X}' -f [uint64]$proc.MainModule.ModuleMemorySize))
Say ('[HDR] lua_State [base+0x7B76AB8] = 0x' + ('{0:X}' -f $script:L))

$warm = RPM ($script:base + [uint64]0x471B5C0) 16
$exp = '4053574883ec484c8b51288d42014c2b'
Say ('[SAN] lua_pcall live= ' + (HexOf $warm 16) + '   expect= ' + $exp)
if ((HexOf $warm 16) -eq $exp) { Say '[SAN] OK - rights fine, Lua cluster warm, decoded dump matches live' }
else { Say '[SAN] WARNING: lua_pcall bytes differ - Lua cluster cold or wrong module' }

try {
  foreach ($pol in @(@(0, 'DEP'), @(2, 'DynamicCode(ACG)'), @(7, 'CFG'), @(8, 'Signature'), @(10, 'ImageLoad'), @(15, 'ShadowStack'))) {
    $buf = NewBytes 8
    $ok = [H35]::GetProcessMitigationPolicy($script:hProc, [int]$pol[0], $buf, [IntPtr]8)
    if ($ok) { Say ('[MIT] ' + $pol[1] + ' = 0x' + ('{0:X8}' -f [System.BitConverter]::ToUInt32($buf, 0))) }
    else { Say ('[MIT] ' + $pol[1] + ' query failed err=' + (GetLastErr)) }
  }
} catch { Say ('[MIT] unavailable: ' + $_.Exception.Message) }

# ---------------------------------------------------------------- [0] warm/cold map
Say ''
Say '== [0] warm/cold map of every page the payload touches =='
$grid = @(
  @{ n='lua_pcall'; rva=0x471B5C0; pg=0x471B000; plain='4053574883ec484c8b51288d42014c2b'; cipher='c35f246fef7c550123d9dc976c3de338' },
  @{ n='lua_pushnil'; rva=0x471BD00; pg=0x471B000; plain='48895c2408574883ec20488b5928488b'; cipher='29d8dfb3e49da52315abf862aaabe207' },
  @{ n='lua_pushcclosure'; rva=0x471B800; pg=0x471B000; plain='40534883ec404c8b4938488974245848'; cipher='d04a4db6c01e5b6caa0f77d195ea229f' },
  @{ n='lua_pushlstring'; rva=0x471BC20; pg=0x471B000; plain='48895c24084889742410574883ec204c'; cipher='a753839f0b4b23d088add397970d6d64' },
  @{ n='lua_tolstring'; rva=0x471B4F0; pg=0x471B000; plain='48895c2408574883ec20488bf9e83edc'; cipher='b82bd1274a2781423ec71253ffdf928d' },
  @{ n='lua_setfield'; rva=0x471D240; pg=0x471D000; plain='48895c240848896c2410488974241857'; cipher='0db852ac11872c3cd36df8fdc9163bf4' },
  @{ n='lua_index2adr'; rva=0x4719140; pg=0x4719000; plain='48895c24104889742418574883ec2048'; cipher='0941a0f24ac624a4daa443061059d6ba' },
  @{ n='lua_nilobj'; rva=0x4718E50; pg=0x4718000; plain='488b81e0000000c3cccccccccccccccc'; cipher='c92312c77385fba60a9eb22e9d917c76' },
  @{ n='lua_checkGC'; rva=0x472DBC0; pg=0x472D000; plain='4053554154415541574883ec204c8b79'; cipher='f63a0c245454caf669e4c639dbb20da8' },
  @{ n='luaS_newlstr'; rva=0x472BA70; pg=0x472B000; plain='48895c240848896c2410488974241848'; cipher='cf91802904d1dac373414fe8be5cf5a6' },
  @{ n='f_parser'; rva=0x4718F60; pg=0x4718000; plain='48895c2408488974241048897c241841'; cipher='411895120971534d07c76aab44dd4c9d' },
  @{ n='luaD_protectedparser'; rva=0x4719A80; pg=0x4719000; plain='40534883ec404c8b49284c2b49584889'; cipher='163ae33dc2390fee33105bf20367062e' },
  @{ n='UnitHealth'; rva=0x1D63190; pg=0x1D63000; plain='488bc4554157488d68a14881ecb80000'; cipher='c9c6e446d06da1b55e499877d641aebf' },
  @{ n='UnitName'; rva=0x1D7B840; pg=0x1D7B000; plain='488bc455564155488da818ffffff4881'; cipher='2e83c1c5ef352c8959a72534568281f8' },
  @{ n='UnitExists'; rva=0x1D596F0; pg=0x1D59000; plain='405553565741564157488d6c24d14881'; cipher='ae9f9f63309ea37d49a8f96a950ac859' },
  @{ n='UnitGUID'; rva=0x1D5AD60; pg=0x1D5A000; plain='405553565741564157488dac2458ffff'; cipher='2dd4d6c8594b83b6a64d6ca53926e925' },
  @{ n='TargetUnit'; rva=0x1B46C60; pg=0x1B46000; plain='405553488d6c24e84881ec1801000048'; cipher='a7e45bdd4bbb6e4b949c82147c31bd11' },
  @{ n='RunScript'; rva=0x136FFF0; pg=0x136F000; plain='40554157488bec4883ec784533ff4889'; cipher='5359a8caea37c766ccf858a612b24b36' },
  @{ n='CastSpellByName'; rva=0x2C24040; pg=0x2C24000; plain='4055535657415541564157488dac2450'; cipher='338d106612e1da54c945ce0657ff315c' },
  @{ n='RunMacro'; rva=0x2CC2DB0; pg=0x2CC2000; plain='48895c2408574883ec20488bd9e83efe'; cipher='2b520f115fa6c54e3e7b37a8b95f3164' },
  @{ n='GetNumCompanions'; rva=0x2C2B5E0; pg=0x2C2B000; plain='48895c240848896c2410488974241848'; cipher='a443b184f1c33985d793e20544346973' },
  @{ n='MoveForwardStart'; rva=0x23C8BE0; pg=0x23C8000; plain='40554157488bec4883ec284889742450'; cipher='9826b250bc98dbcf9d97b4811828f756' },
  @{ n='FnEntityGetGuid'; rva=0x2BBB7F0; pg=0x2BBB000; plain='48895c2418574883ec20488bfa488bd9'; cipher='8c1b1d38524b8b6e4dfc7f6b46102b45' },
  @{ n='GetTime'; rva=0x1B2E050; pg=0x1B2E000; plain='40554157488bec4883ec6848899c2490'; cipher='648102cedb4c0feb2265bdabdaa2f014' },
  @{ n='GetMoney'; rva=0x18C8920; pg=0x18C8000; plain='40554157488bec4883ec6848899c2490'; cipher='fcc6bee2f4853d33a937bc05a84f5415' },
  @{ n='IsLoggedIn'; rva=0x18E0180; pg=0x18E0000; plain='488bc4554157488d68a14881ec980000'; cipher='fd7744201480709344c29ab149d1dd56' },
  @{ n='GetFramerate'; rva=0x10183D0; pg=0x1018000; plain='40554157488bec4883ec6848899c2490'; cipher='78ac158b9250394c90cfbfdb59eabb18' }
)
$warmN = 0; $coldN = 0; $otherN = 0
foreach ($g in $grid) {
  $va = $script:base + [uint64]$g.rva
  $b = RPM $va 16
  $p = ReadProt ($script:base + [uint64]$g.pg)
  $verdict = ''
  if ($null -eq $b) { $verdict = 'COLD(NOACCESS)'; $coldN++ }
  else {
    $h = HexOf $b 16
    if ($h -eq $g.plain) { $verdict = 'WARM(plaintext)'; $warmN++ }
    elseif ($h -eq $g.cipher) { $verdict = 'CIPHER(no access fault yet)'; $coldN++ }
    else { $verdict = ('OTHER ' + $h); $otherN++ }
  }
  $script:mapVerdict[$g.n] = $verdict
  $script:mapRva[$g.n] = [uint64]$g.rva
  Say ('[MAP] ' + $g.n.PadRight(20) + ' 0x' + ('{0:X8}' -f [uint64]$g.rva) + ' prot=0x' + ('{0:X}' -f $p.prot) + ' ' + $verdict)
}
Say ('[MAP] warm=' + $warmN + ' cold=' + $coldN + ' other=' + $otherN + ' of ' + $grid.Count)
if ($script:mapVerdict['UnitHealth'] -like 'WARM*') { Say '[MAP] UnitHealth page is WARM -> the bridge can call it even if foreign-thread cold execution fails' }
if ($script:mapVerdict['UnitHealth'] -like 'COLD*') { Say '[MAP] UnitHealth page is COLD -> [3] tells us whether the packer decrypts it when a foreign thread faults on it' }

# ---------------------------------------------------------------- [1] remote thread
Say ''
Say '== [1] remote thread: instrumented payload =='
$page = [H35]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
if ($page -eq [IntPtr]::Zero) { Say ('[RT] VirtualAllocEx failed err=' + (GetLastErr)); exit 1 }
$script:pageVA = [uint64]$page.ToInt64()
Say ('[RT] stub page at 0x' + ('{0:X}' -f $script:pageVA))

$strBytes = NewBytes 16
[System.Text.Encoding]::ASCII.GetBytes('player').CopyTo($strBytes, 0)
if (-not (WPM ($script:pageVA + [uint64]0x200) $strBytes)) { Say ('[RT] WPM(string) failed err=' + (GetLastErr)); exit 1 }

$stubHexRT   = '4883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b81111111111111111488b40284889433848b802100000000000004889435048b9111111111111111148b8bbbbbbbbbbbbbbbbffd048b803100000000000004889435048b81111111111111111488b53384889502848b804100000000000004889435048b9111111111111111148ba33333333333333334533c048b85555555555555555ffd048b805100000000000004889435048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b806100000000000004889435048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd048b807100000000000004889435089430048b81111111111111111488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9111111111111111148ba44444444444444444533c048b85555555555555555ffd048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd089430448b809100000000000004889435048b91111111111111111baffffffff4533c048b88888888888888888ffd04889433048b80a100000000000004889435048b81111111111111111488b53384889502848b80b100000000000004889435048b8887766554433221148894348b8887766554883c428c3'
$stubHexHJ   = '4883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b81111111111111111488b40284889433848b802100000000000004889435048b9111111111111111148b8bbbbbbbbbbbbbbbbffd048b803100000000000004889435048b81111111111111111488b53384889502848b804100000000000004889435048b9111111111111111148ba33333333333333334533c048b85555555555555555ffd048b805100000000000004889435048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b806100000000000004889435048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd048b807100000000000004889435089430048b81111111111111111488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9111111111111111148ba44444444444444444533c048b85555555555555555ffd048b9111111111111111148ba999999999999999941b80600000048b86666666666666666ffd048b91111111111111111ba0100000041b8010000004533c948b87777777777777777ffd089430448b809100000000000004889435048b91111111111111111baffffffff4533c048b88888888888888888ffd04889433048b80a100000000000004889435048b81111111111111111488b53384889502848b80b100000000000004889435048b8887766554433221148894348f390ebfc'
$stubHexCOLD = '4883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b81111111111111111488b40284889433848b802100000000000004889435048b9111111111111111148b8bbbbbbbbbbbbbbbbffd048b803100000000000004889435048b81111111111111111488b53384889502848b804100000000000004889435048b9111111111111111148baeeeeeeeeeeeeeeee4533c048b85555555555555555ffd048b805100000000000004889435048b91111111111111111ba0000000041b8010000004533c948b87777777777777777ffd048b807100000000000004889435089430848b81111111111111111488b40280fb748f066894b60488b50e8488953584889434048b808100000000000004889435048b81111111111111111488b53384889502848b80b100000000000004889435048b8aa9988776655443348894348b8887766554883c428c3'
$rt = PatchStub $stubHexRT 0
Say ('[RT] payload bytes=' + $rt.Length + ' head=' + (HexOf $rt 16))
if (-not (WPM $script:pageVA (NewBytes 0x80))) { Say '[RT] WPM(scratch zero) failed' }
if (-not (WPM ($script:pageVA + [uint64]0x300) $rt)) { Say ('[RT] WPM(stub) failed err=' + (GetLastErr)); exit 1 }
$rb = RPM ($script:pageVA + [uint64]0x300) 16
Say ('[RT] stub readback= ' + (HexOf $rb 16) + '   (must equal head above)')

$rtStage = [uint64]0
$rtAlive = $false
$tid = [uint32]0
$th = [H35]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:pageVA + [uint64]0x300)), [IntPtr]::Zero, 0, [ref]$tid)
if ($th -eq [IntPtr]::Zero) {
  Say ('[RT] CreateRemoteThread failed err=' + (GetLastErr))
} else {
  $wait = [H35]::WaitForSingleObject($th, 12000)
  $ec = [uint32]0
  [void][H35]::GetExitCodeThread($th, [ref]$ec)
  if ($wait -eq 258) { $rtAlive = $true }
  Say ('[RT] tid=' + $tid + ' wait=' + $wait + ' (0=finished 258=timeout) exit=0x' + ('{0:X}' -f $ec) + ' (0x55667788 = stub reached its final instruction)')
  $sc = RPM $script:pageVA 0x80
  if ($null -eq $sc) { Say '[RT] scratch unreadable - the process is gone' }
  else {
    ShowScratch $sc 'RT'
    $rtStage = U64B $sc 0x50
    $p = U64B $sc 0x30
    if ($p -ne 0) {
      $nm = ReadCStr $p 64
      if ($null -ne $nm) { Say ('[RT] UnitName("player") = "' + $nm + '"') } else { Say '[RT] name pointer not readable' }
    }
  }
  [void][H35]::CloseHandle($th)
}
if ($rtStage -eq 0x100B) { Say '[RT] VERDICT: remote thread executed the whole Lua bridge' }
elseif ($rtStage -eq 0) { Say '[RT] VERDICT: the thread terminated without executing one instruction of our payload' }
else { Say ('[RT] VERDICT: payload died after stage ' + (StageName $rtStage)) }

# ---- [1b] suspended-create probe: is the start address even installed?
if ($rtStage -eq 0 -and -not $rtAlive) {
  Say ''
  Say '== [1b] same start address, created suspended, context inspected before resume =='
  [void](WPM $script:pageVA (NewBytes 0x80))
  $tid2 = [uint32]0
  $th2 = [H35]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:pageVA + [uint64]0x300)), [IntPtr]::Zero, 0x4, [ref]$tid2)
  if ($th2 -eq [IntPtr]::Zero) {
    Say ('[RTb] CreateRemoteThread(SUSPENDED) failed err=' + (GetLastErr))
  } else {
    $ctx = AllocCtx
    $gc = [H35]::GetThreadContext($th2, $ctx)
    $rip = CtxRip $ctx
    $rsp = CtxRsp $ctx
    Say ('[RTb] tid=' + $tid2 + ' GetThreadContext=' + $gc + ' Rip=0x' + ('{0:X}' -f $rip) + ' Rsp=0x' + ('{0:X}' -f $rsp) + ' (Rip should be 0x' + ('{0:X}' -f ($script:pageVA + [uint64]0x300)) + ')')
    $rr = [H35]::ResumeThread($th2)
    Say ('[RTb] ResumeThread=' + $rr)
    $wait2 = [H35]::WaitForSingleObject($th2, 8000)
    $ec2 = [uint32]0
    [void][H35]::GetExitCodeThread($th2, [ref]$ec2)
    $sc2 = RPM $script:pageVA 0x80
    $st2 = [uint64]0
    if ($null -ne $sc2) { $st2 = U64B $sc2 0x50; ShowScratch $sc2 'RTb' }
    Say ('[RTb] wait=' + $wait2 + ' exit=0x' + ('{0:X}' -f $ec2) + ' stage=0x' + ('{0:X}' -f $st2))
    [void][H35]::CloseHandle($th2)
    if ($st2 -eq 0) { Say '[RTb] VERDICT: even a suspended-created thread dies before executing -> something in the target kills foreign thread starts' }
    else { Say '[RTb] VERDICT: suspended-create works; the plain create path was the problem' }
  }
}

# ---------------------------------------------------------------- [2] hijacked thread
$hjStage = [uint64]0
$hjWorked = $false
$hjRestored = $true
$hjTid = 0
if ($rtStage -eq 0x100B) {
  Say ''
  Say '== [2] hijack skipped: the remote-thread path already works =='
} elseif ($SkipHijack) {
  Say ''
  Say '== [2] hijack skipped by -SkipHijack =='
} elseif ($rtAlive) {
  Say ''
  Say '== [2] hijack skipped: the remote thread is still running =='
} else {
  Say ''
  Say '== [2] same payload on a hijacked existing game thread =='
  $threads = @()
  $snap = [H35]::CreateToolhelp32Snapshot(0x4, 0)
  if ($snap -eq [IntPtr]::Zero -or $snap.ToInt64() -eq -1) { Say ('[HJ] snapshot failed err=' + (GetLastErr)) }
  else {
    $te = New-Object THREADENTRY32
    $te.dwSize = 28
    $more = [H35]::Thread32First($snap, [ref]$te)
    while ($more) {
      if ($te.th32OwnerProcessID -eq $script:gamePid) { $threads += [int]$te.th32ThreadID }
      $te.dwSize = 28
      $more = [H35]::Thread32Next($snap, [ref]$te)
    }
    [void][H35]::CloseHandle($snap)
  }
  Say ('[HJ] ' + $threads.Count + ' threads in pid ' + $script:gamePid)

  $samples = @{}
  foreach ($t in $threads) {
    $h = [H35]::OpenThread(0x40, $false, [uint32]$t)
    if ($h -ne [IntPtr]::Zero) {
      $c1 = [long]0; $e1 = [long]0; $k1 = [long]0; $u1 = [long]0
      [void][H35]::GetThreadTimes($h, [ref]$c1, [ref]$e1, [ref]$k1, [ref]$u1)
      $samples[$t] = @{ k = $k1; u = $u1; h = $h }
    }
  }
  Start-Sleep -Milliseconds 400
  $rank = @()
  foreach ($t in $threads) {
    if ($samples.ContainsKey($t)) {
      $s = $samples[$t]
      $c2 = [long]0; $e2 = [long]0; $k2 = [long]0; $u2 = [long]0
      [void][H35]::GetThreadTimes($s.h, [ref]$c2, [ref]$e2, [ref]$k2, [ref]$u2)
      $delta = ($k2 - $s.k) + ($u2 - $s.u)
      $total = $k2 + $u2
      $rank += [pscustomobject]@{ tid = $t; delta = $delta; total = $total; h = $s.h }
    }
  }
  $rank = $rank | Sort-Object @{e = 'delta'; a = $true}, @{e = 'total'; a = $false}
  foreach ($r in $rank) {
    Say ('[HJ] tid ' + $r.tid + '  cpu_delta_400ms=' + [int]($r.delta / 10000) + 'ms  total_cpu=' + [int]($r.total / 10000) + 'ms')
  }
  $cands = @($rank | Select-Object -First 3)
  $attempt = 0
  foreach ($cd in $cands) {
    $attempt++
    Say ('[HJ] --- attempt ' + $attempt + ' on tid ' + $cd.tid + ' ---')
    $h2 = [H35]::OpenThread(0x5A, $false, [uint32]$cd.tid)
    if ($h2 -eq [IntPtr]::Zero) { Say ('[HJ] OpenThread(0x5A) failed err=' + (GetLastErr)); continue }
    [void](WPM $script:pageVA (NewBytes 0x80))
    $hj = PatchStub $stubHexHJ 0
    if (-not (WPM ($script:pageVA + [uint64]0x300) $hj)) { Say ('[HJ] WPM(stub) failed err=' + (GetLastErr)); [void][H35]::CloseHandle($h2); continue }
    $susp = [H35]::SuspendThread($h2)
    if ($susp -eq [uint32]0xFFFFFFFF) { Say ('[HJ] SuspendThread failed err=' + (GetLastErr)); [void][H35]::CloseHandle($h2); continue }
    $ctx = AllocCtx
    if (-not [H35]::GetThreadContext($h2, $ctx)) {
      Say ('[HJ] GetThreadContext failed err=' + (GetLastErr)); [void][H35]::ResumeThread($h2); [void][H35]::CloseHandle($h2); continue
    }
    $orig = AllocCtx
    [Runtime.InteropServices.Marshal]::Copy($ctx, 0, $orig, 0, 1232)
    Say ('[HJ] tid ' + $cd.tid + ' suspended at Rip=0x' + ('{0:X}' -f (CtxRip $ctx)) + ' Rsp=0x' + ('{0:X}' -f (CtxRsp $ctx)))
    CtxSetRip $ctx ($script:pageVA + [uint64]0x300)
    $setOk = [H35]::SetThreadContext($h2, $ctx)
    Say ('[HJ] SetThreadContext=' + $setOk + '  (Rip -> stub)')
    [void][H35]::ResumeThread($h2)
    $st = [uint64]0
    for ($i = 0; $i -lt 50; $i++) {
      Start-Sleep -Milliseconds 100
      $sc = RPM $script:pageVA 0x80
      if ($null -eq $sc) { Say '[HJ] scratch unreadable while polling'; break }
      $st = U64B $sc 0x50
      if ($st -eq 0x100B) { break }
    }
    $susp2 = [H35]::SuspendThread($h2)
    $okRestore = $false
    if ($susp2 -ne [uint32]0xFFFFFFFF) {
      $okRestore = [H35]::SetThreadContext($h2, $orig)
      [void][H35]::ResumeThread($h2)
    }
    Say ('[HJ] restore: suspend=' + $susp2 + ' setctx=' + $okRestore)
    if (-not $okRestore) { $hjRestored = $false }
    $sc = RPM $script:pageVA 0x80
    if ($null -ne $sc) { ShowScratch $sc ('HJ' + $cd.tid) }
    $sc = RPM $script:pageVA 0x30
    if ($null -ne $sc) {
      $p = U64B $sc 0x30
      if ($p -ne 0) {
        $nm = ReadCStr $p 64
        if ($null -ne $nm) { Say ('[HJ' + $cd.tid + '] UnitName("player") = "' + $nm + '"') }
      }
    }
    [void][H35]::CloseHandle($h2)
    if ($st -eq 0x100B) {
      Say ('[HJ] VERDICT: hijacked tid ' + $cd.tid + ' executed the whole Lua bridge')
      $hjStage = $st; $hjWorked = $true; $hjTid = $cd.tid
      break
    } elseif ($st -eq 0) {
      Say ('[HJ] VERDICT: tid ' + $cd.tid + ' never reached our stub')
    } else {
      Say ('[HJ] VERDICT: tid ' + $cd.tid + ' payload died after stage ' + (StageName $st))
      $hjStage = $st
    }
  }
}

# ---------------------------------------------------------------- [3] cold handler test
if (-not $SkipCold) {
  $mech = ''
  if ($rtStage -eq 0x100B) { $mech = 'RT' }
  elseif ($hjWorked) { $mech = 'HJ' }
  $coldName = ''
  $coldRva = [uint64]0
  foreach ($pref in @('GetNumCompanions', 'GetMoney', 'IsLoggedIn', 'GetFramerate', 'GetTime')) {
    if ($script:mapVerdict.ContainsKey($pref) -and $script:mapVerdict[$pref] -like 'COLD*') { $coldName = $pref; $coldRva = $script:mapRva[$pref]; break }
  }
  Say ''
  Say '== [3] call a still-cold handler WITHOUT touching its protection =='
  if ($mech -eq '') { Say '[COLD] skipped: no working execution vector from [1]/[2]' }
  elseif ($coldName -eq '') { Say '[COLD] skipped: no cold zero-arg handler in the map' }
  else {
    Say ('[COLD] mechanism=' + $mech + ' handler=' + $coldName + ' (0x' + ('{0:X}' -f $coldRva) + ')')
    $cold = PatchStub $stubHexCOLD $coldRva
    [void](WPM $script:pageVA (NewBytes 0x80))
    if (-not (WPM ($script:pageVA + [uint64]0x300) $cold)) { Say ('[COLD] WPM(stub) failed err=' + (GetLastErr)) }
    else {
      $cStage = [uint64]0
      if ($mech -eq 'RT') {
        $tid3 = [uint32]0
        $th3 = [H35]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:pageVA + [uint64]0x300)), [IntPtr]::Zero, 0, [ref]$tid3)
        if ($th3 -eq [IntPtr]::Zero) { Say ('[COLD] CreateRemoteThread failed err=' + (GetLastErr)) }
        else {
          $w3 = [H35]::WaitForSingleObject($th3, 10000)
          $e3 = [uint32]0
          [void][H35]::GetExitCodeThread($th3, [ref]$e3)
          Say ('[COLD] tid=' + $tid3 + ' wait=' + $w3 + ' exit=0x' + ('{0:X}' -f $e3))
          [void][H35]::CloseHandle($th3)
        }
      } else {
        $h3 = [H35]::OpenThread(0x5A, $false, [uint32]$hjTid)
        if ($h3 -eq [IntPtr]::Zero) { Say ('[COLD] OpenThread failed err=' + (GetLastErr)) }
        else {
          $ctx3 = AllocCtx
          if ([H35]::SuspendThread($h3) -ne [uint32]0xFFFFFFFF -and [H35]::GetThreadContext($h3, $ctx3)) {
            $orig3 = AllocCtx
            [Runtime.InteropServices.Marshal]::Copy($ctx3, 0, $orig3, 0, 1232)
            CtxSetRip $ctx3 ($script:pageVA + [uint64]0x300)
            [void][H35]::SetThreadContext($h3, $ctx3)
            [void][H35]::ResumeThread($h3)
            for ($i = 0; $i -lt 40; $i++) {
              Start-Sleep -Milliseconds 100
              $sc = RPM $script:pageVA 0x80
              if ($null -eq $sc) { break }
              if ((U64B $sc 0x50) -eq 0x100B) { break }
            }
            $s3 = [H35]::SuspendThread($h3)
            if ($s3 -ne [uint32]0xFFFFFFFF) {
              if (-not [H35]::SetThreadContext($h3, $orig3)) { $hjRestored = $false }
              [void][H35]::ResumeThread($h3)
            } else { $hjRestored = $false }
          } else { [void][H35]::ResumeThread($h3) }
          [void][H35]::CloseHandle($h3)
        }
      }
      $sc = RPM $script:pageVA 0x80
      if ($null -eq $sc) { Say '[COLD] scratch unreadable - process gone' }
      else {
        ShowScratch $sc 'COLD'
        $cStage = U64B $sc 0x50
        if ($cStage -eq 0x100B) {
          Say ('[COLD] VERDICT: the cold page WAS executed (status=' + (I32B $sc 0x08) + ' tt=' + (U16B $sc 0x60) + ' value=' + (F64B $sc 0x58) + ')')
          Say '[COLD] -> the packer decrypts on fault for any thread: every registered handler is callable'
        } elseif ($cStage -eq 0) { Say '[COLD] VERDICT: nothing executed at all in this attempt' }
        else { Say ('[COLD] VERDICT: died after stage ' + (StageName $cStage)) }
        $pg = RPM ($script:base + $coldRva) 16
        $pv = 'unreadable(NOACCESS)'
        if ($null -ne $pg) {
          if ((HexOf $pg 16) -like '48*' -or (HexOf $pg 16) -eq '40554157488bec4883ec6848899c2490') { $pv = 'PLAINTEXT - page got decrypted by the fault' }
          else { $pv = ('still cipher: ' + (HexOf $pg 16)) }
        }
        Say ('[COLD] post-call page read: ' + $pv)
      }
    }
  }
}

# ---------------------------------------------------------------- [4] cleanup
Say ''
Say '== [4] cleanup =='
$safe = $true
if ($rtAlive) { $safe = $false; Say '[CLEAN] the remote thread never finished - page left allocated' }
if (-not $hjRestored) { $safe = $false; Say '[CLEAN] a hijacked thread context was not restored - page left allocated' }
if ($safe) {
  [void][H35]::VirtualFreeEx($script:hProc, $page, [IntPtr]::Zero, 0x8000)
  Say '[CLEAN] stub page freed'
} else {
  Say '[CLEAN] stub page NOT freed (4 KB leak while the game runs - harmless)'
}
[void][H35]::CloseHandle($script:hProc)

Say ''
Say '== SUMMARY =='
$mechFinal = 'none'
if ($rtStage -eq 0x100B) { $mechFinal = 'RT' } elseif ($hjWorked) { $mechFinal = 'HJ' }
Say ('[SUM] map: warm=' + $warmN + ' cold=' + $coldN + ' other=' + $otherN)
Say ('[SUM] RT   : stage=0x' + ('{0:X}' -f $rtStage))
Say ('[SUM] HJ   : stage=0x' + ('{0:X}' -f $hjStage))
Say ('[SUM] COLD : mech=' + $mechFinal)
Say '[DONE] upload C:\ghidra-scripts\hunt35-out.txt'
