# hunt39.ps1 - run our code through MODULE code (the gadget vector)
# ---------------------------------------------------------------------------
# WHAT CRASHED THE CLIENT (hunt35/hunt37), now proven from the log:
#   [RT] payload bytes=560 head=4883ec2848bbaaaaaaaaaaaaaaaa48b8
#   [RT] stub read-back ok=True  first16=4883ec2848bbaaaaaaaaaaaaaaaa48b8
# The payload still contained `mov rbx, 0xAAAAAAAAAAAAAAAA`.  The stub patches
# its template by SEARCHING for 8-byte marker patterns, and
#     U64Bytes 0xAAAAAAAAAAAAAAAA
# cannot bind in PowerShell (the literal does not fit in Int64 and the negative
# Int64 does not convert to [uint64]), so the find-pattern was empty, PatchAll
# did nothing, and it did that SILENTLY.  The stub then dereferenced
# 0xAAAAAAAAAAAAAAA8 on its very first store (the stage stamp) -> access
# violation.  Both crashes came from that one line of my patching code.
#
# So this script patches by OFFSET (no pattern search), asserts that no marker
# byte survives anywhere, and verifies every injected page by read-back before
# any thread is allowed to execute it.
#
# The ladder - each rung must succeed before the next one runs:
#   [PG] page integrity + fake lua_State: write a pattern into a page WE
#        allocate, read it back, and again after 1.5 s (a scanner that zeroes
#        injected memory would show up here).  Also builds a FAKE lua_State at
#        page+0x100 whose top/base differ by 123*24, so lua_gettop on it must
#        return exactly 123.
#   [1]  REMOTE-THREAD PROOF, cannot corrupt anything: CreateRemoteThread with
#        lpStartAddress = wowb+0x471A960 (lua_gettop) and lpParameter = our
#        fake state.  lua_gettop is a leaf that only reads the state, so the
#        argument - not the game - decides the answer.  Exit code 123 proves
#        foreign threads execute our calls; exit code 0 means the thread never
#        ran (that is the hunt34/37 pattern).
#   [2]  REMOTE-THREAD BRIDGE: the real payload (pushnil smoke test, then
#        UnitHealth("player") and UnitName("player") through pushcclosure ->
#        pushlstring -> pcall), with a stage counter stamped after every step so
#        a partial run pinpoints where it stopped.
#   [3]  HIJACK PROOF (only with -AllowHJ): a minimal stub on a borrowed game
#        thread that touches nothing but OUR page (two RIP-relative stores and
#        lua_gettop on the fake state).  This is what hunt35/37 tried to do with
#        the broken payload.
#   [4]  HIJACK BRIDGE (only with -AllowHJ and a successful [3]).
#   [5]  COLD PAGE TEST (only with -AllowCold): pushcclosure into a handler
#        whose page is still NOACCESS.  This is the only rung that can execute
#        ciphertext, so it is off by default.
#   [6]  cleanup, with retries when restoring a hijacked thread context.
#
# Switches: -AllowHJ (enable the hijack ladder - useful when [1] fails),
#           -AllowCold (enable the encrypted-page test), -SkipBridge,
#           -ForceBridge (run [2] even if [1] did not prove out),
#           -ForceExec (ignore the Lua-cluster canary; rung [1] is safe either way).
# WHAT hunt38 PROVED (2026-10-13):
#   [1] CreateRemoteThread(start=module lua_gettop, param=our fake state) -> exit
#       code 123 = the call ran in a brand-new thread.  The remote-thread vector is
#       ALIVE; what never executes is a thread whose START address is our own page
#       ([2] with a read-back-verified payload: exit 0, stage 0, nothing written).
#   [3] a borrowed game thread DID execute our page (probe: lua_gettop -> 123).
#   So the block is on the thread START address, not on thread creation, and the
#   payload/offset patching itself is sound.
#
# hunt39 therefore starts threads ONLY in module code:
#   [1] lua_gettop proof (repeat of hunt38, must return 123)
#   [2] scan the candidate list for a warm `jmp qword ptr [rcx]` (FF 21) whose page
#       is executable - a live byte compare proves the page is decrypted
#   [3] gadget proof: CreateRemoteThread(start=gadget, param=page+0x400) where
#       [page+0x400] = page+0x300, and page+0x300 holds `stamp; mov eax,0x11223344;
#       ret`.  Exit code 0x11223344 = our code ran with a module start address.
#   [4] the Lua bridge through the same gadget (UnitHealth / UnitName)
#   [5] hijack ladder (-AllowHJ, skipped if [4] worked)
#   [6] -TestKill: start a thread at our page whose only instruction is `ret`, then
#       report whether the client survives - this isolates "killed for starting a
#       thread outside the image" from "killed by what the payload did".
#
# PAGE MAP (one 4 KB page):  +0x000 scratch | +0x100 fake lua_State | +0x200 "player"
#   | +0x300 payload (max 576 bytes, ends at +0x53C) | +0x600 pointer -> payload
#   | +0x700 integrity pattern.  (The pointer must live OUTSIDE the payload: the
#   571-byte bridge payload spans +0x300..+0x53C, and putting the pointer at +0x400
#   made the pointer write corrupt the payload - that was the [G4] abort.)
#
# Every section is followed by an [ALIVE] line so the log shows exactly which step
# (if any) the client did not survive.
# RUN: elevated PowerShell, WowB.exe running, character in world, IDLE.
# LOG: C:\ghidra-scripts\hunt39-out.txt
# ---------------------------------------------------------------------------
param([switch]$AllowHJ, [switch]$AllowCold, [switch]$SkipBridge, [switch]$ForceBridge, [switch]$ForceExec, [switch]$TestKill)

$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt39-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function GetLastErr { return [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H38 {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
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
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAlloc(IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
}
[StructLayout(LayoutKind.Sequential)]
public struct THREADENTRY32 {
  public uint dwSize; public uint cntUsage; public uint th32ThreadID; public uint th32OwnerProcessID;
  public int tpBasePri; public int tpDeltaPri; public uint dwFlags;
}
'@

# ---------------------------------------------------------------- helpers
$script:hProc  = [IntPtr]::Zero
$script:base   = [uint64]0
$script:L      = [uint64]0
$script:pageVA = [uint64]0
$script:gamePid = 0
$script:execOk = $false

function NewBytes([int]$n) {
  # the leading comma is load-bearing: without it PowerShell unrolls the array and
  # every P/Invoke fills a throwaway conversion instead of this buffer
  return , ([byte[]]::new($n))
}
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
function I32B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToInt32($b, $o) }
function U16B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToUInt16($b, $o) }
function U64B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToUInt64($b, $o) }
function F64B([byte[]]$b, [int]$o) { return [System.BitConverter]::ToDouble($b, $o) }
function RPM([uint64]$addr, [int]$n) {
  if ($addr -eq 0) { return $null }
  $buf = NewBytes $n
  $got = [IntPtr]::Zero
  if (-not [H38]::ReadProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
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
  if (-not [H38]::WriteProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $buf.Length, [ref]$wrote)) { return $false }
  return ($wrote.ToInt64() -eq $buf.Length)
}
function W64([uint64]$addr, [uint64]$v) { return (WPM $addr ([System.BitConverter]::GetBytes($v))) }
function MBI([uint64]$addr) {
  $buf = NewBytes 0x30
  $r = [H38]::VirtualQueryEx($script:hProc, [IntPtr]([int64]$addr), $buf, [uint64]0x30)
  $o = @{ call = $r; base = [uint64]0; size = [uint64]0; state = 0; prot = 0 }
  if ($r -ge 0x30) {
    $o.base = [System.BitConverter]::ToUInt64($buf, 0x00)
    $o.size = [System.BitConverter]::ToUInt64($buf, 0x18)
    $o.state = [System.BitConverter]::ToUInt32($buf, 0x20)
    $o.prot = [System.BitConverter]::ToUInt32($buf, 0x24)
  }
  return $o
}
function AllocCtx {
  # x64 CONTEXT is 1232 bytes and must be 16-byte aligned -> VirtualAlloc, not byte[]
  $p = [H38]::VirtualAlloc([IntPtr]::Zero, [IntPtr]1232, 0x3000, 0x04)
  if ($p -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
  [Runtime.InteropServices.Marshal]::WriteInt32($p, 0x30, 0x10000B)
  return $p
}
function CtxRip([IntPtr]$p) { return [uint64][Runtime.InteropServices.Marshal]::ReadInt64($p, 0xF8) }
function CtxRsp([IntPtr]$p) { return [uint64][Runtime.InteropServices.Marshal]::ReadInt64($p, 0x98) }
function CtxSetRip([IntPtr]$p, [uint64]$v) { [Runtime.InteropServices.Marshal]::WriteInt64($p, 0xF8, [int64]$v) }
function PSet([byte[]]$buf, [int]$off, [uint64]$v) {
  $b = [System.BitConverter]::GetBytes($v)
  for ($i = 0; $i -lt 8; $i++) { $buf[$off + $i] = $b[$i] }
}
function ResolveSym([string]$sym) {
  if ($sym -eq 'L') { return $script:L }
  if ($sym -eq 'PAGE') { return $script:pageVA }
  if ($sym -eq 'PAGE_PLUS_200') { return ($script:pageVA + [uint64]0x200) }
  if ($sym -eq 'FAKE') { return ($script:pageVA + [uint64]0x100) }
  if ($sym -eq 'HCOLD') { return ($script:base + $script:coldRva) }
  if ($sym -like 'RVA:*') { return ($script:base + [uint64][Convert]::ToUInt64($sym.Substring(4), 16)) }
  return [uint64]0
}
function MarkerRuns([byte[]]$buf) {
  $n = 0
  for ($i = 0; $i -le $buf.Length - 8; $i++) {
    if ($buf[$i] -eq 0xAA -and $buf[$i+1] -eq 0xAA -and $buf[$i+2] -eq 0xAA -and $buf[$i+3] -eq 0xAA -and
        $buf[$i+4] -eq 0xAA -and $buf[$i+5] -eq 0xAA -and $buf[$i+6] -eq 0xAA -and $buf[$i+7] -eq 0xAA) { $n++ }
  }
  return $n
}
function BuildStub([string]$hex, $patches) {
  $c = HexToBytes $hex
  foreach ($p in $patches) {
    $off = [int]$p[0]
    $v = ResolveSym ([string]$p[1])
    if ($v -eq 0) { Say ('[STUB] symbol ' + $p[1] + ' resolved to 0 - aborting'); return $null }
    PSet $c $off $v
  }
  $left = MarkerRuns $c
  if ($left -ne 0) { Say ('[STUB] ' + $left + ' unpatched marker(s) left in the payload - aborting'); return $null }
  return , $c
}
function StageName([uint64]$s) {
  switch ($s) {
    0x1000 { return 'proof-of-execution stamp written (RIP-relative, cannot fault)' }
    0x1001 { return 'entered, rbx set up' }
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
    0x2000 { return 'HIJACK PROOF stamp written' }
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
  Say ('[' + $tag + '] (d) cold handler  status=' + (I32B $sc 0x08) + ' tt=' + (U16B $sc 0x60) + ' value=' + (F64B $sc 0x58))
}
function Alive {
  $p = Get-Process -Id $script:gamePid -ErrorAction SilentlyContinue
  if ($null -eq $p) { return 'DEAD' }
  return ('alive, ' + $p.Threads.Count + ' threads')
}
function ReadCStr([uint64]$addr, [int]$max) {
  if ($addr -lt 0x10000 -or $addr -gt 0x00007FFFFFFF0000) { return $null }
  $b = RPM $addr $max
  if ($null -eq $b) { return $null }
  $end = 0
  while ($end -lt $max -and $b[$end] -ne 0) { $end++ }
  return [System.Text.Encoding]::ASCII.GetString($b, 0, $end)
}

# ---- payload templates (hex) + patch offsets; patched at runtime by field
# (never by pattern search - that is exactly what silently failed before).
$stubHexRT = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b8aaaaaaaaaaaaaaaa488b40284889433848b802100000000000004889435048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048b803100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b804100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b806100000000000004889435048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430048b8aaaaaaaaaaaaaaaa488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd089430448b809100000000000004889435048b9aaaaaaaaaaaaaaaabaffffffff4533c048b8aaaaaaaaaaaaaaaaffd04889433048b80a100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b80b100000000000004889435048b8887766554433221148894348b8887766554883c428c3'
$stubPatchesRT = @(@(17, 'PAGE'), @(41, 'L'), @(73, 'L'), @(83, 'RVA:0x471BD00'), @(109, 'L'), @(141, 'L'), @(151, 'RVA:0x1D63190'), @(164, 'RVA:0x471B800'), @(190, 'L'), @(200, 'PAGE_PLUS_200'), @(216, 'RVA:0x471BC20'), @(242, 'L'), @(266, 'RVA:0x471B5C0'), @(295, 'L'), @(343, 'L'), @(353, 'RVA:0x1D7B840'), @(366, 'RVA:0x471B800'), @(378, 'L'), @(388, 'PAGE_PLUS_200'), @(404, 'RVA:0x471BC20'), @(416, 'L'), @(440, 'RVA:0x471B5C0'), @(469, 'L'), @(487, 'RVA:0x471B4F0'), @(517, 'L'))
$stubHexHJ = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b8aaaaaaaaaaaaaaaa488b40284889433848b802100000000000004889435048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048b803100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b804100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b806100000000000004889435048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430048b8aaaaaaaaaaaaaaaa488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd089430448b809100000000000004889435048b9aaaaaaaaaaaaaaaabaffffffff4533c048b8aaaaaaaaaaaaaaaaffd04889433048b80a100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b80b100000000000004889435048b8887766554433221148894348f390ebfc'
$stubPatchesHJ = @(@(17, 'PAGE'), @(41, 'L'), @(73, 'L'), @(83, 'RVA:0x471BD00'), @(109, 'L'), @(141, 'L'), @(151, 'RVA:0x1D63190'), @(164, 'RVA:0x471B800'), @(190, 'L'), @(200, 'PAGE_PLUS_200'), @(216, 'RVA:0x471BC20'), @(242, 'L'), @(266, 'RVA:0x471B5C0'), @(295, 'L'), @(343, 'L'), @(353, 'RVA:0x1D7B840'), @(366, 'RVA:0x471B800'), @(378, 'L'), @(388, 'PAGE_PLUS_200'), @(404, 'RVA:0x471BC20'), @(416, 'L'), @(440, 'RVA:0x471B5C0'), @(469, 'L'), @(487, 'RVA:0x471B4F0'), @(517, 'L'))
$stubHexPROBE = '48c70545fdffff0020000048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048890540fdfffff390ebfc'
$stubPatchesPROBE = @(@(13, 'FAKE'), @(23, 'RVA:0x471A960'))
$stubHexCOLD = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaaba0000000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430848b8aaaaaaaaaaaaaaaa488b40280fb748f066894b60488b50e8488953584889434048b80b100000000000004889435048b8aaaaaaaaaaaaaaaa48b8aa9988776655443348894348b8887766554883c428c3'
$stubPatchesCOLD = @(@(17, 'PAGE'), @(41, 'L'), @(51, 'HCOLD'), @(64, 'RVA:0x471B800'), @(90, 'L'), @(114, 'RVA:0x471B5C0'), @(143, 'L'), @(191, 'L'))
$stubHexEXIT = '48c70545fdffff00300000b844332211c3'
$stubPatchesEXIT = @()
\n$stubHexRT = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b8aaaaaaaaaaaaaaaa488b40284889433848b802100000000000004889435048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048b803100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b804100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b806100000000000004889435048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430048b8aaaaaaaaaaaaaaaa488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd089430448b809100000000000004889435048b9aaaaaaaaaaaaaaaabaffffffff4533c048b8aaaaaaaaaaaaaaaaffd04889433048b80a100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b80b100000000000004889435048b8887766554433221148894348b8887766554883c428c3'
$stubPatchesRT = @(@(17, 'PAGE'), @(41, 'L'), @(73, 'L'), @(83, 'RVA:0x471BD00'), @(109, 'L'), @(141, 'L'), @(151, 'RVA:0x1D63190'), @(164, 'RVA:0x471B800'), @(190, 'L'), @(200, 'PAGE_PLUS_200'), @(216, 'RVA:0x471BC20'), @(242, 'L'), @(266, 'RVA:0x471B5C0'), @(295, 'L'), @(343, 'L'), @(353, 'RVA:0x1D7B840'), @(366, 'RVA:0x471B800'), @(378, 'L'), @(388, 'PAGE_PLUS_200'), @(404, 'RVA:0x471BC20'), @(416, 'L'), @(440, 'RVA:0x471B5C0'), @(469, 'L'), @(487, 'RVA:0x471B4F0'), @(517, 'L'))
$stubHexHJ = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b8aaaaaaaaaaaaaaaa488b40284889433848b802100000000000004889435048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048b803100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b804100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b806100000000000004889435048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430048b8aaaaaaaaaaaaaaaa488b40280fb748f066894b18488b50e8488953104889434048b808100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa41b80600000048b8aaaaaaaaaaaaaaaaffd048b9aaaaaaaaaaaaaaaaba0100000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd089430448b809100000000000004889435048b9aaaaaaaaaaaaaaaabaffffffff4533c048b8aaaaaaaaaaaaaaaaffd04889433048b80a100000000000004889435048b8aaaaaaaaaaaaaaaa488b53384889502848b80b100000000000004889435048b8887766554433221148894348f390ebfc'
$stubPatchesHJ = @(@(17, 'PAGE'), @(41, 'L'), @(73, 'L'), @(83, 'RVA:0x471BD00'), @(109, 'L'), @(141, 'L'), @(151, 'RVA:0x1D63190'), @(164, 'RVA:0x471B800'), @(190, 'L'), @(200, 'PAGE_PLUS_200'), @(216, 'RVA:0x471BC20'), @(242, 'L'), @(266, 'RVA:0x471B5C0'), @(295, 'L'), @(343, 'L'), @(353, 'RVA:0x1D7B840'), @(366, 'RVA:0x471B800'), @(378, 'L'), @(388, 'PAGE_PLUS_200'), @(404, 'RVA:0x471BC20'), @(416, 'L'), @(440, 'RVA:0x471B5C0'), @(469, 'L'), @(487, 'RVA:0x471B4F0'), @(517, 'L'))
$stubHexPROBE = '48c70545fdffff0020000048b9aaaaaaaaaaaaaaaa48b8aaaaaaaaaaaaaaaaffd048890540fdfffff390ebfc'
$stubPatchesPROBE = @(@(13, 'FAKE'), @(23, 'RVA:0x471A960'))
$stubHexCOLD = '48c70545fdffff001000004883ec2848bbaaaaaaaaaaaaaaaa48b801100000000000004889435048b9aaaaaaaaaaaaaaaa48baaaaaaaaaaaaaaaaa4533c048b8aaaaaaaaaaaaaaaaffd048b805100000000000004889435048b9aaaaaaaaaaaaaaaaba0000000041b8010000004533c948b8aaaaaaaaaaaaaaaaffd048b807100000000000004889435089430848b8aaaaaaaaaaaaaaaa488b40280fb748f066894b60488b50e8488953584889434048b80b100000000000004889435048b8aaaaaaaaaaaaaaaa48b8aa9988776655443348894348b8887766554883c428c3'
$stubPatchesCOLD = @(@(17, 'PAGE'), @(41, 'L'), @(51, 'HCOLD'), @(64, 'RVA:0x471B800'), @(90, 'L'), @(114, 'RVA:0x471B5C0'), @(143, 'L'), @(191, 'L'))

# ---------------------------------------------------------------- attach + canary
Say '[HDR] hunt39 - module-start gadget vector (offset-patched payloads, proof before trust)'
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the client and rerun'; exit 1 }
if ($proc -is [array]) { Say ('[HDR] WARNING: ' + $proc.Count + ' WowB processes; using the first'); $proc = $proc[0] }
$script:gamePid = $proc.Id
$script:hProc = [H38]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H38]::OpenProcess(0x043A, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H38]::OpenProcess(0x0418, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { Say ('[HDR] OpenProcess failed err=' + (GetLastErr)); exit 1 }
$script:base = [uint64]$proc.MainModule.BaseAddress.ToInt64()
Say ('[HDR] pid=' + $proc.Id + ' base=0x' + ('{0:X}' -f $script:base) + ' moduleSize=0x' + ('{0:X}' -f [uint64]$proc.MainModule.ModuleMemorySize) + ' threads=' + $proc.Threads.Count)
$script:L = U64A ($script:base + [uint64]0x7B76AB8)
Say ('[HDR] lua_State [base+0x7B76AB8] = 0x' + ('{0:X}' -f $script:L))

$probeBuf = NewBytes 4
if (-not ($probeBuf -is [byte[]])) { Say ('[CAN] FATAL: buffer type is ' + $probeBuf.GetType().Name); exit 1 }
$mz = RPM $script:base 16
Say ('[CAN] base+0 = ' + (HexOf $mz 16))
if ($null -eq $mz -or (HexOf $mz 2) -ne '4d5a') { Say '[CAN] FATAL: base+0 is not an MZ header - reads untrustworthy, aborting'; exit 1 }
$lua = RPM ($script:base + [uint64]0x471A960) 16
$expWarm = '488b512848b8abaaaaaaaaaaaa2a482b'      # 16 bytes, from the decoded dump
$expCold = 'd8cd98580328593680f39ac81c7098e3'      # 16 bytes, the shipped (encrypted) file
$live16 = HexOf $lua 16
Say ('[CAN] lua_gettop live   = ' + $live16)
Say ('[CAN]        warm should = ' + $expWarm)
Say ('[CAN]        cold looks  = ' + $expCold)
$luaWarm = ($live16 -eq $expWarm)
$luaCold = ($live16 -eq $expCold)
if ($luaWarm) { Say '[CAN] Lua cluster is WARM (plaintext, matches the decoded dump)' }
elseif ($luaCold) { Say '[CAN] Lua cluster is COLD (still the encrypted shipped bytes) - a call would execute ciphertext' }
else { Say '[CAN] Lua cluster reads as NEITHER warm plaintext nor shipped ciphertext - inspect the bytes above' }
if ($script:L -ne 0) {
  $top = U64A ($script:L + [uint64]0x28)
  $bas = U64A ($script:L + [uint64]0x30)
  $delta = [uint64]0
  if ($top -gt $bas) { $delta = $top - $bas }
  $mod = $delta % [uint64]24
  Say ('[CAN] L->top=0x' + ('{0:X}' -f $top) + ' L->base=0x' + ('{0:X}' -f $bas) + ' delta=0x' + ('{0:X}' -f $delta) + ' (mod 24 = ' + $mod + '; 0 is normal, the stack is just empty)')
}
$script:execOk = ($luaWarm -and $script:L -ne 0)
if (-not $luaWarm -and $ForceExec) { $script:execOk = ($script:L -ne 0); Say '[CAN] -ForceExec: overriding the canary' }
if (-not $script:execOk) { Say '[CAN] execution sections will be skipped (cold cluster or null state); -ForceExec overrides' }

# ---------------------------------------------------------------- [PG] page + fake state
Say ''
Say '== [PG] page integrity + fake lua_State =='
$page = [H38]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
if ($page -eq [IntPtr]::Zero) { Say ('[PG] VirtualAllocEx failed err=' + (GetLastErr)); exit 1 }
$script:pageVA = [uint64]$page.ToInt64()
Say ('[PG] page = 0x' + ('{0:X}' -f $script:pageVA))
$strBytes = NewBytes 16
[System.Text.Encoding]::ASCII.GetBytes('player').CopyTo($strBytes, 0)
if (-not (WPM ($script:pageVA + [uint64]0x200) $strBytes)) { Say '[PG] WPM(string) failed'; exit 1 }
# fake lua_State at +0x100: top - base = 123*24 -> lua_gettop must return 123
$fake = $script:pageVA + [uint64]0x100
if (-not (W64 ($fake + [uint64]0x28) ($script:pageVA + [uint64]0xC88))) { Say '[PG] WPM(fake top) failed'; exit 1 }
if (-not (W64 ($fake + [uint64]0x30) $fake)) { Say '[PG] WPM(fake base) failed'; exit 1 }
$pat = NewBytes 16
for ($i = 0; $i -lt 16; $i++) { $pat[$i] = [byte](0x40 + $i) }
if (-not (WPM ($script:pageVA + [uint64]0x700) $pat)) { Say '[PG] WPM(pattern) failed'; exit 1 }
$r1 = RPM ($script:pageVA + [uint64]0x700) 16
Say ('[PG] pattern read-back = ' + (HexOf $r1 16) + ' match=' + ((HexOf $r1 16) -eq (HexOf $pat 16)))
Start-Sleep -Milliseconds 1500
$r2 = RPM ($script:pageVA + [uint64]0x700) 16
Say ('[PG] after 1.5 s       = ' + (HexOf $r2 16) + ' match=' + ((HexOf $r2 16) -eq (HexOf $pat 16)))
$m = MBI $script:pageVA
Say ('[PG] MBI base=0x' + ('{0:X}' -f $m.base) + ' size=0x' + ('{0:X}' -f $m.size) + ' state=0x' + ('{0:X}' -f $m.state) + ' prot=0x' + ('{0:X}' -f $m.prot))
if ((HexOf $r1 16) -ne (HexOf $pat 16)) { Say '[PG] FATAL: cannot read back our own page - aborting'; exit 1 }

# ---------------------------------------------------------------- [1] proof
Say ''
Say '== [1] remote-thread proof: lua_gettop on a FAKE state must return 123 =='
$rtProof = $false
if (-not $script:execOk) {
  Say '[P1] skipped: canary failed'
} else {
  $tids = [uint32]0
  $th = [H38]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + [uint64]0x471A960)), [IntPtr]([int64]$fake), 0, [ref]$tids)
  if ($th -eq [IntPtr]::Zero) {
    Say ('[P1] CreateRemoteThread failed err=' + (GetLastErr))
  } else {
    $w = [H38]::WaitForSingleObject($th, 5000)
    $ec = [uint32]0
    [void][H38]::GetExitCodeThread($th, [ref]$ec)
    [void][H38]::CloseHandle($th)
    Say ('[P1] tid=' + $tids + ' wait=' + $w + ' exit=' + $ec + ' (expect exactly 123)')
    if ($ec -eq 123) { $rtProof = $true; Say '[P1] VERDICT: a fresh thread in module code executes the call we point it at' }
    elseif ($ec -eq 0) { Say '[P1] VERDICT: the thread did not run our start address' }
    else { Say '[P1] VERDICT: unexpected exit code' }
  }
}
Say ('[ALIVE] after [1]: ' + (Alive))

# ---------------------------------------------------------------- [2] gadget hunt
Say ''
Say '== [2] find a warm `jmp qword ptr [rcx]` (FF 21) in module code =='
$gadgetRva = [uint64]0
$gadgetProt = 0
$cands = @(0xB125, 0x10A341, 0x7932CD, 0xBD15E3, 0xFBF06D, 0x143A7DA, 0x1778AE3, 0x1C1BFDD, 0x23D5878, 0x2539BED, 0x28092F3, 0x280C94A, 0x280D033, 0x280E27A, 0x280F112, 0x2E5802D, 0x2F5B6DC, 0x356AE83, 0x39620DE, 0x3FC59F7, 0x44E2434, 0x484C66F, 0x49FAC77, 0x49FADBF, 0x49FAE1F, 0x49FAE8F, 0x49FAEF7, 0x49FAF37, 0x49FB9CF, 0x49FBA2F, 0x49FBB9F, 0x49FBBCF, 0x4CC8057, 0x4CCADB7, 0x4CF9C12, 0x53A9B3D, 0x5454A71, 0x5475D5D, 0x5842E23, 0x585564B)
if (-not $script:execOk) { Say '[G] skipped: canary failed' }
else {
  $tried = 0
  foreach ($c in $cands) {
    $tried++
    $b2 = RPM ($script:base + [uint64]$c) 2
    if ($null -eq $b2) { continue }
    if ((HexOf $b2 2) -ne 'ff21') { continue }
    $m = MBI ($script:base + [uint64]$c)
    $x = [int]$m.prot -band 0xF0
    if ($x -ne 0x10 -and $x -ne 0x20 -and $x -ne 0x40 -and $x -ne 0x80) {
      Say ('[G] 0x' + ('{0:X}' -f $c) + ' reads ff21 but prot=0x' + ('{0:X}' -f $m.prot) + ' is not executable - next')
      continue
    }
    $gadgetRva = [uint64]$c
    $gadgetProt = [int]$m.prot
    break
  }
  if ($gadgetRva -eq 0) { Say ('[G] no usable gadget among ' + $tried + ' candidates') }
  else { Say ('[G] gadget = 0x' + ('{0:X}' -f $gadgetRva) + ' (module+0x' + ('{0:X}' -f $gadgetRva) + ', page prot=0x' + ('{0:X}' -f $gadgetProt) + ', bytes ff 21 verified live)') }
}

# ---------------------------------------------------------------- [3] gadget proof
Say ''
Say '== [3] gadget proof: thread starts in module code, jumps into OUR page =='
$gadgetProof = $false
if ($gadgetRva -eq 0 -or -not $script:execOk) {
  Say '[G3] skipped'
} else {
  $exitStub = BuildStub $stubHexEXIT $stubPatchesEXIT
  $ptrAddr = $script:pageVA + [uint64]0x600
  $stubAddr = $script:pageVA + [uint64]0x300
  $ok = (WPM $stubAddr $exitStub) -and (W64 $ptrAddr $stubAddr)
  $back = RPM $stubAddr $exitStub.Length
  if (-not $ok -or $null -eq $back -or (HexOf $back $exitStub.Length) -ne (HexOf $exitStub $exitStub.Length)) {
    Say '[G3] ABORT: could not place/verify the payload'
  } else {
    Say ('[G3] payload 0x' + ('{0:X}' -f $stubAddr) + ' = ' + (HexOf $exitStub $exitStub.Length) + ' ; [0x' + ('{0:X}' -f $ptrAddr) + '] = 0x' + ('{0:X}' -f $stubAddr))
    $tids = [uint32]0
    $th = [H38]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$ptrAddr), 0, [ref]$tids)
    if ($th -eq [IntPtr]::Zero) {
      Say ('[G3] CreateRemoteThread(gadget) failed err=' + (GetLastErr))
    } else {
      $w = [H38]::WaitForSingleObject($th, 5000)
      $ec = [uint32]0
      [void][H38]::GetExitCodeThread($th, [ref]$ec)
      [void][H38]::CloseHandle($th)
      $sc3 = RPM $script:pageVA 0x60
      $stage = [uint64]0
      if ($null -ne $sc3) { $stage = U64B $sc3 0x50 }
      Say ('[G3] tid=' + $tids + ' wait=' + $w + ' exit=0x' + ('{0:X}' -f $ec) + ' (0x11223344 = OUR code ran)  stage=0x' + ('{0:X}' -f $stage))
      if ($ec -eq 0x11223344 -and $stage -eq 0x3000) {
        $gadgetProof = $true
        Say '[G3] VERDICT: module-start thread -> our code. The gadget vector WORKS.'
      } else {
        Say '[G3] VERDICT: our code did not run through the gadget'
      }
    }
  }
}
Say ('[ALIVE] after [3]: ' + (Alive))

# ---------------------------------------------------------------- [4] bridge via gadget
$rtStage = [uint64]0
$rtBridgeOk = $false
if ($SkipBridge) {
  Say ''
  Say '== [4] bridge skipped by -SkipBridge =='
} elseif (-not $gadgetProof) {
  Say ''
  Say '== [4] bridge skipped: the gadget proof did not succeed =='
} else {
  Say ''
  Say '== [4] Lua bridge through the gadget (pushnil, UnitHealth, UnitName) =='
  $rt = BuildStub $stubHexRT $stubPatchesRT
  $ptrAddr = $script:pageVA + [uint64]0x600
  $stubAddr = $script:pageVA + [uint64]0x300
  if ($null -eq $rt) { Say '[G4] payload build failed' }
  elseif (-not ((WPM $stubAddr $rt) -and (W64 $ptrAddr $stubAddr))) { Say '[G4] WPM failed' }
  else {
    $back = RPM $stubAddr $rt.Length
    if ($null -eq $back -or (HexOf $back $rt.Length) -ne (HexOf $rt $rt.Length)) { Say '[G4] ABORT: payload read-back mismatch' }
    else {
      $sc0 = RPM $script:pageVA 0x80
      $stageBefore = [uint64]0
      if ($null -ne $sc0) { $stageBefore = U64B $sc0 0x50 }
      Say ('[G4] payload bytes=' + $rt.Length + ' read-back ok, stage before=0x' + ('{0:X}' -f $stageBefore))
      $tids = [uint32]0
      $th = [H38]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$ptrAddr), 0, [ref]$tids)
      if ($th -eq [IntPtr]::Zero) { Say ('[G4] CreateRemoteThread failed err=' + (GetLastErr)) }
      else {
        $w = [H38]::WaitForSingleObject($th, 12000)
        $ec = [uint32]0
        [void][H38]::GetExitCodeThread($th, [ref]$ec)
        [void][H38]::CloseHandle($th)
        Say ('[G4] tid=' + $tids + ' wait=' + $w + ' exit=0x' + ('{0:X}' -f $ec) + ' (0x55667788 = the whole bridge ran)')
      }
      $sc = RPM $script:pageVA 0x80
      if ($null -eq $sc) { Say '[G4] scratch unreadable' }
      else {
        ShowScratch $sc 'G4'
        $rtStage = U64B $sc 0x50
        $p = U64B $sc 0x30
        if ($p -ne 0) {
          $nm = ReadCStr $p 64
          if ($null -ne $nm) { Say ('[G4] UnitName("player") = "' + $nm + '"') }
        }
        if ($rtStage -eq 0x100B) { $rtBridgeOk = $true; Say '[G4] VERDICT: the full Lua bridge executed from a fresh thread via the gadget' }
        elseif ($rtStage -eq 0) { Say '[G4] VERDICT: nothing executed' }
        else { Say ('[G4] VERDICT: the payload stopped after stage ' + (StageName $rtStage)) }
      }
    }
  }
}
Say ('[ALIVE] after [4]: ' + (Alive))

# ---------------------------------------------------------------- hijack helper
function HijackRun([int]$tid, [byte[]]$stub, [uint64]$expectStage, [int]$timeoutMs, [string]$tag) {
  $res = @{ ok = $false; stage = [uint64]0; restored = $false; why = ''; rip0 = [uint64]0; rsp0 = [uint64]0; scratch = $null }
  $h = [H38]::OpenThread(0x5A, $false, [uint32]$tid)
  if ($h -eq [IntPtr]::Zero) { $res.why = 'OpenThread err=' + (GetLastErr); return $res }
  $susp = [H38]::SuspendThread($h)
  if ($susp -eq [uint32]0xFFFFFFFF) { $res.why = 'SuspendThread failed'; [void][H38]::CloseHandle($h); return $res }
  $ctx = AllocCtx
  $orig = AllocCtx
  if (-not [H38]::GetThreadContext($h, $ctx)) {
    $res.why = 'GetThreadContext failed'
    [void][H38]::ResumeThread($h); [void][H38]::CloseHandle($h); return $res
  }
  [Runtime.InteropServices.Marshal]::Copy($ctx, 0, $orig, 0, 1232)
  $res.rip0 = CtxRip $ctx
  $res.rsp0 = CtxRsp $ctx
  if (-not (WPM ($script:pageVA + [uint64]0x300) $stub)) {
    $res.why = 'WPM(payload) failed'
    [void][H38]::ResumeThread($h); [void][H38]::CloseHandle($h); return $res
  }
  $back = RPM ($script:pageVA + [uint64]0x300) $stub.Length
  if ($null -eq $back -or (HexOf $back $stub.Length) -ne (HexOf $stub $stub.Length)) {
    $res.why = 'payload read-back mismatch - refusing to move any RIP'
    [void][H38]::ResumeThread($h); [void][H38]::CloseHandle($h); return $res
  }
  CtxSetRip $ctx ($script:pageVA + [uint64]0x300)
  if (-not [H38]::SetThreadContext($h, $ctx)) {
    $res.why = 'SetThreadContext(fake) failed'
    [void][H38]::ResumeThread($h); [void][H38]::CloseHandle($h); return $res
  }
  [void][H38]::ResumeThread($h)
  $st = [uint64]0
  for ($i = 0; $i -lt [int]($timeoutMs / 100) -and $timeoutMs -gt 0; $i++) {
    Start-Sleep -Milliseconds 100
    $sc = RPM $script:pageVA 0x80
    if ($null -eq $sc) { break }
    $st = U64B $sc 0x50
    if ($st -eq $expectStage) { break }
  }
  # stop the borrowed thread and put its original context back (with retries)
  $restored = $false
  for ($try = 0; $try -lt 6 -and -not $restored; $try++) {
    $s2 = [H38]::SuspendThread($h)
    if ($s2 -eq [uint32]0xFFFFFFFF) { Start-Sleep -Milliseconds 100; continue }
    $restored = [H38]::SetThreadContext($h, $orig)
    [void][H38]::ResumeThread($h)
    if (-not $restored) { Start-Sleep -Milliseconds 100 }
  }
  $res.restored = $restored
  $res.stage = $st
  $res.ok = ($st -eq $expectStage)
  $sc = RPM $script:pageVA 0x80
  $res.scratch = $sc
  if ($res.why -eq '' -and -not $restored) { $res.why = 'context NOT restored' }
  [void][H38]::CloseHandle($h)
  return $res
}

# ---------------------------------------------------------------- [5] hijack proof
$hjProof = $false
$hjTid = 0
$hjCand = @()
if ($rtBridgeOk) {
  Say ''
  Say '[HJ] hijack ladder not needed: the gadget bridge already ran'
} elseif (-not $AllowHJ) {
  Say ''
  Say '== [5] hijack ladder skipped (add -AllowHJ; skipped automatically when the gadget bridge worked) =='
} elseif (-not $script:execOk) {
  Say ''
  Say '== [5] hijack ladder skipped: canary failed =='
} else {
  Say ''
  Say '== [3] hijack proof on a borrowed thread (payload touches nothing but our page) =='
  $snap = [H38]::CreateToolhelp32Snapshot(0x4, 0)
  $threads = @()
  if ($snap -ne [IntPtr]::Zero -and $snap.ToInt64() -ne -1) {
    $te = New-Object THREADENTRY32
    $te.dwSize = 28
    $more = [H38]::Thread32First($snap, [ref]$te)
    while ($more) {
      if ($te.th32OwnerProcessID -eq $script:gamePid) { $threads += [int]$te.th32ThreadID }
      $te.dwSize = 28
      $more = [H38]::Thread32Next($snap, [ref]$te)
    }
    [void][H38]::CloseHandle($snap)
  }
  Say ('[HJ] ' + $threads.Count + ' threads in pid ' + $script:gamePid)
  $samples = @{}
  foreach ($t in $threads) {
    $hh = [H38]::OpenThread(0x40, $false, [uint32]$t)
    if ($hh -ne [IntPtr]::Zero) {
      $c1 = [long]0; $e1 = [long]0; $k1 = [long]0; $u1 = [long]0
      [void][H38]::GetThreadTimes($hh, [ref]$c1, [ref]$e1, [ref]$k1, [ref]$u1)
      $samples[$t] = @{ h = $hh; k = $k1; u = $u1 }
    }
  }
  Start-Sleep -Milliseconds 500
  $rank = @()
  foreach ($t in $threads) {
    if ($samples.ContainsKey($t)) {
      $s = $samples[$t]
      $c2 = [long]0; $e2 = [long]0; $k2 = [long]0; $u2 = [long]0
      [void][H38]::GetThreadTimes($s.h, [ref]$c2, [ref]$e2, [ref]$k2, [ref]$u2)
      $rank += [pscustomobject]@{ tid = $t; delta = (($k2 - $s.k) + ($u2 - $s.u)); total = ($k2 + $u2) }
    }
  }
  # most recently active threads first: this time the sort keys are spelled out
  $sorted = $rank | Sort-Object -Property @{Expression = 'delta'; Descending = $true}, @{Expression = 'total'; Descending = $true}
  foreach ($r in ($sorted | Select-Object -First 6)) {
    Say ('[HJ] tid ' + $r.tid + '  cpu_delta_500ms=' + [int]($r.delta / 10000) + 'ms  total_cpu=' + [int]($r.total / 10000) + 'ms')
  }
  $probe = BuildStub $stubHexPROBE $stubPatchesPROBE
  if ($null -eq $probe) { Say '[HJ] probe build failed' }
  else {
    $hjCand = @($sorted | Where-Object { $_.delta -gt 0 } | Select-Object -First 3)
    if ($hjCand.Count -eq 0) {
      Say '[HJ] no thread used any CPU in the sample window - using the top three by total CPU'
      $hjCand = @($sorted | Select-Object -First 3)
    }
    foreach ($cd in $hjCand) {
      Say ('[HJ] --- proof attempt on tid ' + $cd.tid + ' ---')
      [void](WPM $script:pageVA (NewBytes 0x80))
      $r = HijackRun $cd.tid $probe ([uint64]0x2000) 2000 ('HJ' + $cd.tid)
      Say ('[HJ] tid=' + $cd.tid + ' was at Rip=0x' + ('{0:X}' -f $r.rip0) + ' Rsp=0x' + ('{0:X}' -f $r.rsp0) + '  reason=' + $r.why)
      if ($null -ne $r.scratch) {
        $got = U64B $r.scratch 0x68
        Say ('[HJ] proof: stage=0x' + ('{0:X}' -f $r.stage) + ' lua_gettop result stored=' + $got + ' (123 = proof)  context_restored=' + $r.restored)
      }
      if ($r.ok -and $null -ne $r.scratch -and (U64B $r.scratch 0x68) -eq 123) {
        $hjProof = $true; $hjTid = $cd.tid
        Say ('[HJ] VERDICT: tid ' + $cd.tid + ' executed our payload - the hijack vector works')
        break
      } else {
        Say ('[HJ] VERDICT: tid ' + $cd.tid + ' did not complete the proof')
      }
    }
  }
}

# ---------------------------------------------------------------- [6] hijack bridge
if ($hjProof -and -not $SkipBridge) {
  Say ''
  Say '== [6] hijack bridge on tid ' + $hjTid + ' =='
  $hj = BuildStub $stubHexHJ $stubPatchesHJ
  if ($null -eq $hj) { Say '[HJ4] payload build failed' }
  else {
    [void](WPM $script:pageVA (NewBytes 0x80))
    $r = HijackRun $hjTid $hj ([uint64]0x100B) 8000 ('HJ4')
    Say ('[HJ4] reason=' + $r.why + ' stage=0x' + ('{0:X}' -f $r.stage) + ' context_restored=' + $r.restored)
    if ($null -ne $r.scratch) {
      ShowScratch $r.scratch 'HJ4'
      $p = U64B $r.scratch 0x30
      if ($p -ne 0) {
        $nm = ReadCStr $p 64
        if ($null -ne $nm) { Say ('[HJ4] UnitName("player") = "' + $nm + '"') }
      }
      if ($r.stage -eq 0x100B) { Say '[HJ4] VERDICT: the whole bridge ran on a borrowed game thread' }
    }
  }
} elseif ($AllowHJ -and -not $hjProof) {
  Say ''
  Say '== [6] hijack bridge skipped: the proof in [5] did not succeed =='
}

# ---------------------------------------------------------------- [5] cold handler
if (-not $AllowCold) {
  Say ''
  Say '== [5] cold-handler test skipped (add -AllowCold; it executes an encrypted page and may kill the client) =='
} elseif (-not ($script:execOk -and $rtProof)) {
  Say ''
  Say '== [5] cold-handler test skipped: rung [1] is not proven =='
} else {
  Say ''
  Say '== [5] cold-handler test: call a NOACCESS handler without flipping its protection =='
  $coldName = ''
  $script:coldRva = [uint64]0
  foreach ($cn in @(@(0x2C2B5E0, 'GetNumCompanions'), @(0x18C8920, 'GetMoney'), @(0x18E0180, 'IsLoggedIn'), @(0x1B2E050, 'GetTime'))) {
    $b = RPM ($script:base + [uint64]$cn[0]) 4
    if ($null -eq $b) { $coldName = $cn[1]; $script:coldRva = [uint64]$cn[0]; break }
  }
  if ($coldName -eq '') { Say '[COLD] no cold page among the candidates right now - nothing to test' }
  else {
    Say ('[COLD] handler=' + $coldName + ' rva=0x' + ('{0:X}' -f $script:coldRva) + ' (page is NOACCESS)')
    $cold = BuildStub $stubHexCOLD $stubPatchesCOLD
    if ($null -eq $cold) { Say '[COLD] payload build failed' }
    else {
      [void](WPM $script:pageVA (NewBytes 0x80))
      if (-not (WPM ($script:pageVA + [uint64]0x300) $cold)) { Say '[COLD] WPM failed' }
      else {
        $tid = [uint32]0
        $th = [H38]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:pageVA + [uint64]0x300)), [IntPtr]::Zero, 0, [ref]$tid)
        if ($th -eq [IntPtr]::Zero) { Say ('[COLD] CreateRemoteThread failed err=' + (GetLastErr)) }
        else {
          $w = [H38]::WaitForSingleObject($th, 10000)
          $ec = [uint32]0
          [void][H38]::GetExitCodeThread($th, [ref]$ec)
          Say ('[COLD] tid=' + $tid + ' wait=' + $w + ' exit=0x' + ('{0:X}' -f $ec))
          [void][H38]::CloseHandle($th)
        }
        $sc = RPM $script:pageVA 0x80
        if ($null -eq $sc) { Say '[COLD] scratch unreadable - the client is likely gone' }
        else {
          ShowScratch $sc 'COLD'
          $st = U64B $sc 0x50
          if ($st -eq 0x100B) {
            Say '[COLD] VERDICT: the encrypted page WAS executed (the packer decrypts on fault for any thread)'
          } elseif ($st -eq 0) { Say '[COLD] VERDICT: nothing executed' }
          else { Say ('[COLD] VERDICT: died after stage ' + (StageName $st)) }
        }
      }
    }
  }
}

# ---------------------------------------------------------------- [7] -TestKill
if (-not $TestKill) {
  Say ''
  Say '== [7] -TestKill skipped (add -TestKill to start one thread at our page running a bare `ret`) =='
} else {
  Say ''
  Say '== [7] -TestKill: does a thread STARTED in our page kill the client? =='
  $killBytes = HexToBytes 'c3'
  if (-not (WPM ($script:pageVA + [uint64]0x300) $killBytes)) { Say '[K] WPM failed' }
  else {
    $tids = [uint32]0
    $th = [H38]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:pageVA + [uint64]0x300)), [IntPtr]::Zero, 0, [ref]$tids)
    if ($th -eq [IntPtr]::Zero) { Say ('[K] CreateRemoteThread failed err=' + (GetLastErr)) }
    else {
      $w = [H38]::WaitForSingleObject($th, 3000)
      $ec = [uint32]0
      [void][H38]::GetExitCodeThread($th, [ref]$ec)
      [void][H38]::CloseHandle($th)
      Say ('[K] tid=' + $tids + ' wait=' + $w + ' exit=' + $ec + ' (a bare ret returns 0)')
    }
    Start-Sleep -Milliseconds 2500
    Say ('[K] 2.5 s later the client is ' + (Alive))
  }
}

# ---------------------------------------------------------------- [8] cleanup
Say ''
Say '== [6] cleanup =='
[void][H38]::VirtualFreeEx($script:hProc, $page, [IntPtr]::Zero, 0x8000)
Say '[CLEAN] stub page freed'
[void][H38]::CloseHandle($script:hProc)
Say ''
Say '== SUMMARY =='
Say ('[SUM] canary(exec ok)=' + $script:execOk + '  remote-thread proof=' + $rtProof + '  gadget proof=' + $gadgetProof + '  gadget bridge=' + $rtBridgeOk + '  hijack proof=' + $hjProof)
Say ('[SUM] bridge stage=0x' + ('{0:X}' -f $rtStage))
Say '[DONE] upload C:\ghidra-scripts\hunt39-out.txt'
