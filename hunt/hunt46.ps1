# hunt46.ps1 - the bridge goes resident: ONE thread, a mailbox, many calls
# ---------------------------------------------------------------------------
# WHAT hunt46 PROVED (its log: [SUM] execOk=True tramp=1 callOk=True snapshotOk=11/11 stuck=False)
#
#   * the x64 call machinery works.  getfield(globals,name) + pushlstring(arg) +
#     lua_pcall through the mode-1 trampoline returned real data, 11/11:
#       GetTime()=205448.515   UnitHealth("player")=73   UnitLevel=2  GetMoney=24
#       UnitExists("player")=true   UnitExists("target")=false
#       UnitClass 2 results ("HUNTER"/"Hunter")   UnitName="Zero"
#       UnitGUID="Player-70-00000005"   IsLoggedIn=true
#     one injected thread per call, 70 threads before and after, no crash.
#   * allocation is NOT the blocker: luaS_newlstr interned a brand-new string from
#     our thread.  pushcclosure's old wedge was therefore not "a foreign thread may
#     not allocate"; it fits the one thing that must not happen from a frame-less
#     foreign call: a collector step.  hunt40/41 forced one (they wrote the
#     threshold); hunt46 never triggered one, because lua_pcall always hands the
#     call a real CallInfo.
#   * handler results carry a flag bit in the type word: UnitHealth came back with
#     tt=0x103 while GetTime came back with tt=3.  0x103 & 0xFF = 3 = number and the
#     value was really 73.0, so the reader must compare (tt & 0xFF) and ignore the
#     rest (the flag marks secure/unit data).
#
# WHAT hunt46 DOES
#
#   Exactly what the x64 reader/executor needs next: one injected thread, started
#   once, serving an arbitrary number of calls from a mailbox in our own page.
#
#     CMD @+0xC8   0 = idle, 1 = run the block, 0xFF = exit
#     SEQ @+0xD0   the driver bumps this per command
#     ACK @+0xD8   the payload copies SEQ here once the results are complete,
#                  so the driver never reads a half-written result
#     calls @+0xE0 / idles @+0x118 counters, stage stamps 0x4100 (waiting),
#     0x4101 (command taken), hunt46's 0x4002..0x4020 inside the call, 0x40FF exit
#     the payload reads hunt46's field layout unchanged (+0x00..+0xC0)
#     tickFn @+0xF8 / sleepFn @+0x100 / deadline @+0x108 / spin budget @+0x110:
#     kernel32!GetTickCount64 and kernel32!Sleep when they resolve, so the idle
#     loop neither burns a core nor can outlive the driver
#
#   * [4] starts that thread and shows it idle-waiting (stage 0x4100)
#   * [5] the first command through the mailbox: GetTime()
#   * [6] a reader-shaped loop - <Iterations> rounds of six read-only calls - with
#     per-command latency statistics and a stack-discipline check
#     (top_after must equal top_before + 0x18 * nresults)
#   * [7] what idling costs: polls/second + the client's CPU time over 2 s
#   * [8] clean shutdown (CMD=0xFF -> exit code 0x4200CAFE) and the failsafe: a
#     second resident thread with a 2 s deadline must exit by itself, so a driver
#     that dies cannot leave a spinning thread in the client
#   * the page is then freed - safe, because both threads exited
#
# RUN
#   .\hunt46.ps1                     ~15 s
#   .\hunt46.ps1 -Iterations 200     much more traffic through the same thread
#   .\hunt46.ps1 -PatchGuard         mode 0: patch the validator out for the run
#   .\hunt46.ps1 -Diag               also re-run the allocation / factory probes
#
# IF SOMETHING FAULTS the payload's own deadline exits it; if the client dies,
# read the last stage stamp and restart it (a fault parks the thread and the client
# handler kills the process a few seconds later - that delay is not a hang).
# ---------------------------------------------------------------------------
param([switch]$Diag, [switch]$SkipBridge, [switch]$ForceExec, [int]$Start = 0, [switch]$PatchGuard, [int]$Iterations = 30, [int]$Serve = 60, [int]$SleepMs = 1)



$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt46-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function GetLastErr { return [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H46 {
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
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr a, IntPtr sz, uint prot, ref uint old);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAlloc(IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool TerminateThread(IntPtr h, uint code);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] public static extern IntPtr GetModuleHandleA(string name);
  [DllImport("kernel32.dll", CharSet=CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr mod, string name);
  [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
}
'@

# ---------------------------------------------------------------- helpers
$script:hProc  = [IntPtr]::Zero
if (-not ([System.Management.Automation.PSTypeName]'H46').Type) {
  Say '[HDR] FATAL: the [H46] P/Invoke class did not load (Add-Type above failed) - nothing else can run'
  exit 1
}
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
  if (-not [H46]::ReadProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
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
  if (-not [H46]::WriteProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $buf.Length, [ref]$wrote)) { return $false }
  return ($wrote.ToInt64() -eq $buf.Length)
}
function W64([uint64]$addr, [uint64]$v) { return (WPM $addr ([System.BitConverter]::GetBytes($v))) }
function MBI([uint64]$addr) {
  $buf = NewBytes 0x30
  $r = [H46]::VirtualQueryEx($script:hProc, [IntPtr]([int64]$addr), $buf, [uint64]0x30)
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
  $p = [H46]::VirtualAlloc([IntPtr]::Zero, [IntPtr]1232, 0x3000, 0x04)
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


# ---------------------------------------------------------------- where is a thread stuck?
function WhereIs([IntPtr]$hThread, [string]$tag) {
  # A payload thread that is still alive after the timeout is either spinning or
  # blocked.  Suspend it, read its context, read the bytes at RIP, then let it go
  # again.  This names the function it is stuck in - no guessing.
  $ctx = AllocCtx
  if ($ctx -eq [IntPtr]::Zero) { Say ('[' + $tag + '] could not allocate a CONTEXT buffer'); return }
  $msz = [uint64]0
  $pg = Get-Process -Id $script:gamePid -ErrorAction SilentlyContinue
  if ($null -ne $pg) { $msz = [uint64]$pg.MainModule.ModuleMemorySize }
  try {
    for ($k = 1; $k -le 3; $k++) {
      $sr = [int64][H46]::SuspendThread($hThread)
      $susp = $true
      if ($sr -lt 0) {
        $susp = $false
        Say ('[' + $tag + '] sample ' + $k + ': SuspendThread FAILED raw=' + $sr + ' err=' + (GetLastErr) + ' handle=0x' + ('{0:X}' -f [int64]$hThread))
        Say ('[' + $tag + ']           (handle kept alive on purpose - a faulting thread is parked by the client handler)')
      }
      $ok = [H46]::GetThreadContext($hThread, $ctx)
      if ($ok) {
        $rip = CtxRip $ctx
        $rsp = CtxRsp $ctx
        $where = ''
        if ($rip -ge $script:base -and $rip -lt ($script:base + $msz)) { $where = ' = module+0x' + ('{0:X}' -f ($rip - $script:base)) }
        $regs = ''
        foreach ($pr in @(@('rax', 0x78), @('rcx', 0x80), @('rdx', 0x88), @('rbx', 0x90), @('r13', 0xE0), @('r14', 0xE8))) {
          $regs += ' ' + $pr[0] + '=0x' + ('{0:X}' -f [uint64][Runtime.InteropServices.Marshal]::ReadInt64($ctx, [int]$pr[1]))
        }
        Say ('[' + $tag + '] sample ' + $k + ': RIP=0x' + ('{0:X}' -f $rip) + $where + '  RSP=0x' + ('{0:X}' -f $rsp) + $regs)
        $code = RPM $rip 16
        if ($null -ne $code) { Say ('[' + $tag + ']             bytes at RIP: ' + (HexOf $code 16)) }
        $stk = RPM $rsp 24
        if ($null -ne $stk) { Say ('[' + $tag + ']             bytes at RSP: ' + (HexOf $stk 24)) }
      } else {
        Say ('[' + $tag + '] sample ' + $k + ': GetThreadContext failed err=' + (GetLastErr))
      }
      if ($susp) { [void][H46]::ResumeThread($hThread) } else { break }
      Start-Sleep -Milliseconds 250
    }
  } finally {
    [void]($ctx)
  }
}

# ---------------------------------------------------------------- the guard bypass (opt-in)
function PatchGuard {
  # module+0x1D8E80 is the routine every VM call entry point hands its return address to.
  # Its entry is  40 53 48 83 EC 20 48 8B D1  (push rbx; sub rsp,0x20; mov rdx,rcx).
  # Writing 31 C0 C3 (xor eax,eax; ret) makes it return at once: the push rbx never
  # happens so the plain ret pops exactly the return address the caller pushed, and
  # eax = 0 is benign - every call site discards or overwrites it.
  # The 8 original bytes are put back by UnpatchGuard when the probes are done.
  $addr = $script:base + [uint64]0x1D8E80
  $orig = RPM $addr 8
  if ($null -eq $orig) { Say '[GP] could not read the validator entry'; return $false }
  $old = [uint32]0
  if (-not [H46]::VirtualProtectEx($script:hProc, [IntPtr]([int64]$addr), [IntPtr]8, 0x40, [ref]$old)) {
    Say ('[GP] VirtualProtectEx failed err=' + (GetLastErr)); return $false
  }
  $patch = HexToBytes '31c0c3909090'
  if (-not (WPM $addr $patch)) { Say '[GP] patch write failed'; return $false }
  $back = RPM $addr 8
  Say ('[GP] validator module+0x1D8E80: was ' + (HexOf $orig 8) + '  now ' + (HexOf $back 8))
  if ((HexOf $back 6) -eq '31c0c3909090') {
    $script:guardOrig = $orig
    $script:guardPatched = $true
    Say '[GP] PATCHED - VM call entries now return without the call-site check'
    return $true
  }
  Say '[GP] read-back mismatch - the patch is NOT active'
  return $false
}
function UnpatchGuard {
  if (-not $script:guardPatched) { return }
  $addr = $script:base + [uint64]0x1D8E80
  $restore = NewBytes 8
  for ($i = 0; $i -lt 8; $i++) { $restore[$i] = $script:guardOrig[$i] }
  if (WPM $addr $restore) { Say ('[GP] validator restored: ' + (HexOf (RPM $addr 8) 8)) }
  else { Say '[GP] RESTORE FAILED - restart the client' }
  $script:guardPatched = $false
}

# ---------------------------------------------------------------- attach + canary
Say '[HDR] hunt46 - trampoline call + stuck-thread RIP sampling + GetTime/UnitHealth/allocation probes'
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the client and rerun'; exit 1 }
if ($proc -is [array]) { Say ('[HDR] WARNING: ' + $proc.Count + ' WowB processes; using the first'); $proc = $proc[0] }
$script:gamePid = $proc.Id
$script:hProc = [H46]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H46]::OpenProcess(0x043A, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H46]::OpenProcess(0x0418, $false, $proc.Id) }
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
# ---- collector state: totalbytes = G+0x78, GCthreshold = G+0x70 (proven from
# ---- luaM_realloc_ 'add [G+0x78], size-delta' and pushcclosure's 'cmp [G+0x78],[G+0x70]')
$script:G = [uint64]0
$script:G70 = [uint64]0
$script:G78 = [uint64]0
$script:G88 = [uint64]0
if ($script:L -ne 0) {
  $script:G = U64A ($script:L + [uint64]0x38)
  if ($script:G -ne 0) {
    $script:G70 = U64A ($script:G + [uint64]0x70)
    $script:G78 = U64A ($script:G + [uint64]0x78)
    $script:G88 = U64A ($script:G + [uint64]0x88)
    Say ('[CAN] G=0x' + ('{0:X}' -f $script:G) + '  totalbytes(G+0x78)=' + $script:G78 + '  threshold(G+0x70)=' + $script:G70 + '  debt(G+0x88)=' + $script:G88)
    if ($script:G78 -ge $script:G70) { Say '[CAN] the collector is DUE (totalbytes >= threshold)' }
    else { Say '[CAN] the collector is not due at this instant (totalbytes < threshold)' }
  } else { Say '[CAN] G is null - cannot read the collector state' }
}
$script:execOk = ($luaWarm -and $script:L -ne 0)
if (-not $luaWarm -and $ForceExec) { $script:execOk = ($script:L -ne 0); Say '[CAN] -ForceExec: overriding the canary' }
if (-not $script:execOk) { Say '[CAN] execution sections will be skipped (cold cluster or null state); -ForceExec overrides' }


# ---------------------------------------------------------------- value rendering
function TTName([int]$tt) {
  switch ($tt) {
    0 { return 'nil' }
    1 { return 'boolean' }
    2 { return 'lightuserdata' }
    3 { return 'number' }
    4 { return 'string' }
    5 { return 'table' }
    6 { return 'function' }
    7 { return 'userdata' }
    8 { return 'thread' }
    default { return ('tt=' + $tt) }
  }
}
function AsciiRun([byte[]]$b) {
  $best = ''
  $cur = ''
  foreach ($c in $b) {
    if ($c -ge 0x20 -and $c -lt 0x7f) { $cur += [char]$c } else { if ($cur.Length -gt $best.Length) { $best = $cur }; $cur = '' }
  }
  if ($cur.Length -gt $best.Length) { $best = $cur }
  return $best
}
function LuaStr([uint64]$ts) {
  if ($ts -eq 0) { return '(null TString)' }
  $b = RPM $ts 0x60
  if ($null -eq $b) { return ('0x' + ('{0:X}' -f $ts) + ' (TString unreadable)') }
  return ('0x' + ('{0:X}' -f $ts) + ' "' + (AsciiRun $b) + '"  first48=' + (HexOf $b 0x30))
}

# ---------------------------------------------------------------- [0] read-only state
Say ''
Say '== [0] read-only state: allocator, globals table, callee pages =='
$script:G = [uint64]0
$script:stuck = $false
$script:startSec = $Start
$script:lastTh = [IntPtr]::Zero
$script:guardPatched = $false
$script:guardOrig = $null
$script:trampX = [uint64]0
$script:trampMode = 0
if ($script:L -ne 0) { $script:G = U64A ($script:L + [uint64]0x38) }
if ($script:L -eq 0) { Say '[ST] no lua_State - cannot continue'; exit 1 }
$msz = [uint64]$proc.MainModule.ModuleMemorySize
$fa = U64A ($script:G + [uint64]0x10)
$ud = U64A ($script:G + [uint64]0x18)
$loc = 'OUTSIDE the module (CRT / private heap)'
if ($fa -ge $script:base -and $fa -lt ($script:base + $msz)) { $loc = 'module+0x' + ('{0:X}' -f ($fa - $script:base)) }
Say ('[ST] G            = 0x' + ('{0:X}' -f $script:G))
Say ('[ST] G->frealloc  = 0x' + ('{0:X}' -f $fa) + '   ' + $loc + '   <- every Lua allocation goes through this')
Say ('[ST] G->ud        = 0x' + ('{0:X}' -f $ud))
$gtv = RPM ($script:L + [uint64]0x90) 16
if ($null -ne $gtv) {
  $gtt = [int](U16B $gtv 8)
  Say ('[ST] L+0x90 (L->l_gt) value=0x' + ('{0:X}' -f (U64B $gtv 0)) + ' tt=' + $gtt + ' (' + (TTName $gtt) + ')   <- the globals table getfield will read')
}
Say ('[ST] L->top=0x' + ('{0:X}' -f (U64A ($script:L + [uint64]0x28))) + '  L->base=0x' + ('{0:X}' -f (U64A ($script:L + [uint64]0x30))) + '  L->ci=0x' + ('{0:X}' -f (U64A ($script:L + [uint64]0x40))))
$t1 = U64A ($script:L + [uint64]0x28)
Start-Sleep -Milliseconds 150
$t2 = U64A ($script:L + [uint64]0x28)
Start-Sleep -Milliseconds 150
$t3 = U64A ($script:L + [uint64]0x28)
$idle = (($t1 -eq $t2) -and ($t2 -eq $t3))
Say ('[ST] L->top sampled 3x: 0x' + ('{0:X}' -f $t1) + ' 0x' + ('{0:X}' -f $t2) + ' 0x' + ('{0:X}' -f $t3) + '  stable=' + $idle + '   (unstable = the client is running Lua right now; the calls below would race with it)')
$callees = @(
  @{ n = 'lua_getfield(3-arg)'; rva = 0x471A370; why = 'the way in: globals[name]' },
  @{ n = 'getfield w/ len'; rva = 0x471A500; why = 'fallback: same lookup, explicit length' },
  @{ n = 'lua_pushlstring'; rva = 0x471BC20; why = 'pushes the argument (no alloc if interned)' },
  @{ n = 'lua_pcall'; rva = 0x471B5C0; why = 'calls the handler with a real CallInfo' },
  @{ n = 'luaS_newlstr'; rva = 0x472BA70; why = 'interning (-Diag allocation probe)' },
  @{ n = 'CClosure factory'; rva = 0x4720B50; why = 'what pushcclosure wedges in (-Diag probe)' },
  @{ n = 'luaM_realloc_'; rva = 0x472FB50; why = 'calls qword [G+0x10]' },
  @{ n = 'UnitHealth'; rva = 0x1D63190; why = 'target handler' },
  @{ n = 'UnitName'; rva = 0x1D7B840; why = 'target handler' },
  @{ n = 'UnitExists'; rva = 0x1D596F0; why = 'target handler' },
  @{ n = 'lua_gettop'; rva = 0x471A960; why = 'the proven leaf probe' }
)
$coldL = @()
foreach ($c in $callees) {
  $va = $script:base + [uint64]$c.rva
  $b8 = RPM $va 8
  $m = MBI $va
  $stt = 'WARM'
  if ($null -eq $b8) { $stt = 'NOACCESS(COLD)'; $coldL += $c.n }
  Say ('[CAL] ' + $c.n.PadRight(20) + ' 0x' + ('{0:X8}' -f [uint64]$c.rva) + ' prot=0x' + ('{0:X}' -f $m.prot) + ' ' + $stt + '   (' + $c.why + ')')
}
if ($coldL.Count -eq 0) { Say '[CAL] every callee is warm' } else { Say ('[CAL] COLD CALLEES: ' + ($coldL -join ', ')) }

# ---------------------------------------------------------------- [1] remote-thread proof
Say ''
Say '== [1] remote-thread proof: lua_gettop on a FAKE state must return 123 =='
$fake = [uint64]0
$script:execOk2 = $false
if (-not $script:execOk) {
  Say '[P1] skipped: the canary failed (the Lua cluster is not warm)'
} else {
  $fake = [H46]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
  if ($fake -eq [IntPtr]::Zero) {
    Say ('[P1] VirtualAllocEx failed err=' + (GetLastErr))
  } else {
    $fakeQ = [uint64]$fake.ToInt64()
    if (-not ((W64 ($fakeQ + [uint64]0x28) ($fakeQ + [uint64]0xC88)) -and (W64 ($fakeQ + [uint64]0x30) $fakeQ))) {
      Say '[P1] could not write the fake state'
    } else {
      $tid1 = [uint32]0
      $th1 = [H46]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + [uint64]0x471A960)), [IntPtr]([int64]$fake), 0, [ref]$tid1)
      if ($th1 -eq [IntPtr]::Zero) {
        Say ('[P1] CreateRemoteThread failed err=' + (GetLastErr))
      } else {
        $w1 = [H46]::WaitForSingleObject($th1, 5000)
        $ec1 = [uint32]0
        [void][H46]::GetExitCodeThread($th1, [ref]$ec1)
        [void][H46]::CloseHandle($th1)
        Say ('[P1] tid=' + $tid1 + ' wait=' + $w1 + ' exit=' + $ec1 + ' (expect exactly 123)')
        if ($ec1 -eq 123) { $script:execOk2 = $true; Say '[P1] VERDICT: a fresh thread in module code executes the call we point it at' }
        else { Say '[P1] VERDICT: unexpected exit code' }
      }
    }
  }
}
Say ('[ALIVE] after [1]: ' + (Alive))

# ---------------------------------------------------------------- [2] gadget hunt
Say ''
Say '== [2] find a warm `jmp qword ptr [rcx]` (FF 21) in module code =='
$gadgetRva = [uint64]0
$gadgetProt = 0
$cands = @(0x7932CD, 0xBD15E3, 0xB125, 0x10A341, 0xFBF06D, 0x143A7DA, 0x1778AE3, 0x1C1BFDD, 0x23D5878, 0x2539BED, 0x28092F3, 0x280C94A, 0x280D033, 0x280E27A, 0x280F112, 0x2E5802D, 0x2F5B6DC, 0x356AE83, 0x39620DE, 0x3FC59F7, 0x44E2434, 0x484C66F, 0x49FAC77, 0x49FADBF, 0x49FAE1F, 0x49FAE8F, 0x49FAEF7, 0x49FAF37, 0x49FB9CF, 0x49FBA2F, 0x49FBB9F, 0x49FBBCF, 0x4CC8057, 0x4CCADB7, 0x4CF9C12, 0x53A9B3D, 0x5454A71, 0x5475D5D, 0x5842E23, 0x585564B)
if (-not $script:execOk) { Say '[G] skipped: canary failed' }
else {
  foreach ($c in $cands) {
    $b2 = RPM ($script:base + [uint64]$c) 2
    if ($null -eq $b2) { continue }
    if ((HexOf $b2 2) -ne 'ff21') { continue }
    $m = MBI ($script:base + [uint64]$c)
    $x = [int]$m.prot -band 0xF0
    if ($x -ne 0x10 -and $x -ne 0x20 -and $x -ne 0x40 -and $x -ne 0x80) { continue }
    $gadgetRva = [uint64]$c
    $gadgetProt = [int]$m.prot
    break
  }
  if ($gadgetRva -eq 0) { Say '[G] no usable gadget among the candidates' }
  else { Say ('[G] gadget = module+0x' + ('{0:X}' -f $gadgetRva) + '  page prot=0x' + ('{0:X}' -f $gadgetProt) + '  bytes ff 21 verified live') }
}

# ---------------------------------------------------------------- [3] page + payload
Say ''
Say '== [3] page, payload and the gadget jump proof =='
$gadgetProof = $false
$pageVA2 = [H46]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]0x2000, 0x3000, 0x40)
if ($pageVA2 -eq [IntPtr]::Zero) { Say ('[PG] VirtualAllocEx failed err=' + (GetLastErr)); exit 1 }
$script:pageVA = [uint64]$pageVA2.ToInt64()
$script:ptrSlot = $script:pageVA + [uint64]0x600
$script:payloadVA = $script:pageVA + [uint64]0x800
Say ('[PG] page=0x' + ('{0:X}' -f $script:pageVA) + '  block=0x' + ('{0:X}' -f $script:ptrSlot) + '  payload=0x' + ('{0:X}' -f $script:payloadVA))
$pat = NewBytes 16
for ($i = 0; $i -lt 16; $i++) { $pat[$i] = [byte](0x40 + $i) }
[void](WPM ($script:pageVA + [uint64]0x40) $pat)
$r1 = RPM ($script:pageVA + [uint64]0x40) 16
Say ('[PG] pattern read-back = ' + (HexOf $r1 16) + ' match=' + ((HexOf $r1 16) -eq (HexOf $pat 16)))
$exitStub = HexToBytes '48c70545fdffff00300000b844332211c3'
[void](WPM ($script:pageVA + [uint64]0x300) $exitStub)
[void](W64 $script:ptrSlot ($script:pageVA + [uint64]0x300))
$bk3 = RPM ($script:pageVA + [uint64]0x300) $exitStub.Length
if ($null -eq $bk3 -or (HexOf $bk3 $exitStub.Length) -ne (HexOf $exitStub $exitStub.Length)) {
  Say '[G3] ABORT: the exit stub did not read back'
} elseif ($gadgetRva -eq 0) {
  Say '[G3] skipped: no gadget'
} else {
  $tid3 = [uint32]0
  $th3 = [H46]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$script:ptrSlot), 0, [ref]$tid3)
  if ($th3 -eq [IntPtr]::Zero) {
    Say ('[G3] CreateRemoteThread failed err=' + (GetLastErr))
  } else {
    $w3 = [H46]::WaitForSingleObject($th3, 5000)
    $ec3 = [uint32]0
    [void][H46]::GetExitCodeThread($th3, [ref]$ec3)
    [void][H46]::CloseHandle($th3)
    $st3 = U64A ($script:pageVA + [uint64]0x50)
    Say ('[G3] tid=' + $tid3 + ' wait=' + $w3 + ' exit=0x' + ('{0:X}' -f $ec3) + ' stage=0x' + ('{0:X}' -f $st3))
    if ($ec3 -eq 0x11223344 -and $st3 -eq 0x3000) { $gadgetProof = $true; Say '[G3] VERDICT: module-start thread -> our page. The gadget vector WORKS.' }
    else { Say '[G3] VERDICT: our code did not run through the gadget' }
  }
}
$script:payloadOk = $false
if ($gadgetProof) {
  $pl = HexToBytes '4889cb4883ec2848c783e00000000000000048c783180100000000000048c783d80000000000000048c783c80000000000000048c7437000410000488b83c80000004885c00f84d4010000483dff0000000f841802000048c74370014100004c8b73084d8b7e284c89bb9000000048c783c00000000000000048837b40020f832d0100004c89f1baeed8ffff4c8b431048837b4001750c4c8b4b48488b4378ffd0eb06488b4378ffd048c74370024000004d8b6e284c89abc0000000498b45e8488983a0000000410fb745f0668983a800000048837b30000f850c01000048c743700340000048837b200074144c89f1488b53184c8b4338488b8380000000ffd048c74370044000004c8bab880000004c89f1488b53204c8b43284531c94989e44883e4f048c743704a00040048837b6000750541ffd5eb154883ec28488d2d0a000000554c8b5358415241ffe54c89e448c743704b0004004889839800000048c74370054000004d8b6e284c89abc000000048837b28017c70498b45e8488983a0000000410fb745f0668983a800000048837b28027c52498b45d0488983b0000000410fb745d8668983b8000000eb3948c74370104000004c89f148837b4003740a488b53104c8b4348eb08488b53184c8b4350488b4378ffd0488983a000000048c7437011400000498b46284889436848c7437020400000488b83d0000000488983d800000048ff83e000000048c783c800000000000000e91cfeffff48ff8318010000488b83f80000004885c0740dffd0483b83080100007332eb10488b8318010000483b83100100007320488b83000100004885c0740d8b8b24010000ffd0e9d3fdfffff390e9ccfdffff48c74370ff4000004883c428b8feca0042c3'
  $plPlaced = WPM $script:payloadVA $pl
  $plBack = RPM $script:payloadVA $pl.Length
  $plMatch = ($null -ne $plBack -and (HexOf $plBack $pl.Length) -eq (HexOf $pl $pl.Length))
  $m = MBI $script:pageVA
  Say ('[PG] bridge payload ' + $pl.Length + ' bytes placed=' + $plPlaced + ' read-back=' + $plMatch + '  page prot=0x' + ('{0:X}' -f $m.prot))
  if ($plMatch) { $script:payloadOk = $true } else { Say '[PG] FATAL: the bridge payload did not read back - aborting'; exit 1 }
} else {
  Say '[PG] the bridge payload was not placed: no gadget proof'
}
Say ('[ALIVE] after [3]: ' + (Alive))

# ---------------------------------------------------------------- stage names
function StageName46([uint64]$s) {
  switch ($s) {
    0x4001 { return 'entered the payload' }
    0x4002 { return 'lua_getfield returned' }
    0x4003 { return 'before lua_pushlstring' }
    0x4004 { return 'before the call - a wedge HERE means the protector rejected the return address' }
    0x4004A { return 'entering lua_pcall - a wedge HERE is inside the call machinery/protector' }
    0x4004B { return 'lua_pcall returned - the protector let it through' }
    0x4005 { return 'the call returned, results captured' }
    0x4010 { return 'raw call (luaS_newlstr / the CClosure factory)' }
    0x4011 { return 'raw call returned' }
    0x4020 { return 'COMPLETE - ran to the end' }
    0x4100 { return 'RESIDENT - waiting for a command' }
    0x4101 { return 'a command was taken from the mailbox' }
    0x40FF { return 'exiting cleanly (failsafe or 0xFF)' }
    default { return '(no stage stamp)' }
  }
}

# ---------------------------------------------------------------- [2b] the caller check
Say ''
Say '== [2b] the protector call-site check and the trampoline that satisfies it =='
$script:trampX = [uint64]0
$script:trampMode = 0
Say '[PT] luaD_call / lua_pcall / luaD_pcall / rawrunprotected all end by entering 0x1D8E80 with their return address.'
Say '[PT] That routine range-checks it against a descriptor, classifies the call shape (E8 / FF2) and, on a'
Say '[PT] mismatch, falls into 0x1D9115 `movzx eax,[rbx]` with rbx=0 - a deliberate null dereference (hunt42 died there).'
$u32 = RPM ($script:base + [uint64]0x5FF6B30) 4
if ($null -ne $u32) {
  $v32 = [uint32][System.BitConverter]::ToUInt32($u32, 0)
  Say ('[PT] dword[base+0x5FF6B30] = 0x' + ('{0:X}' -f $v32) + '  (the range descriptor; 0 would skip the check)')
  if ($v32 -ge 0x10000) {
    $ds = RPM ([uint64]$v32) 16
    if ($null -ne $ds) {
      $q0 = U64B $ds 0
      $q1 = U64B $ds 8
      Say ('[PT] descriptor[0]=0x' + ('{0:X}' -f $q0) + ' [8]=0x' + ('{0:X}' -f $q1) + '   as {base,size}=0x' + ('{0:X}' -f $q0) + '..0x' + ('{0:X}' -f ($q0 + $q1)) + '   as {size,base}=0x' + ('{0:X}' -f $q1) + '..0x' + ('{0:X}' -f ($q1 + $q0)))
    }
  }
} else { Say '[PT] the descriptor could not be read' }
$g2 = U64A ($script:base + [uint64]0x5FF0B08)
Say ('[PT] qword[base+0x5FF0B08] = 0x' + ('{0:X}' -f $g2) + '   (its byte +0x50B feeds the final canary test)')
if ($g2 -ge 0x10000 -and $g2 -lt 0x7FFFFFFFF000) {
  $cb = RPM ($g2 + [uint64]0x50B) 1
  if ($null -ne $cb) { Say ('[PT] canary byte [that+0x50B] = 0x' + ('{0:X}' -f $cb[0])) }
}
function CheckTramp([uint64]$X) {
  if ($X -lt 0x10000) { return $false }
  $b = RPM ($X - [uint64]5) 6
  if ($null -eq $b) { return $false }
  if ($b[0] -ne 0xE8) { return $false }
  if ($b[5] -ne 0xC3) { return $false }
  $m = MBI $X
  $x = [int]$m.prot -band 0xF0
  if ($x -ne 0x10 -and $x -ne 0x20 -and $x -ne 0x40 -and $x -ne 0x80) { return $false }
  return $true
}
$cand = $script:base + [uint64]0x471A983
$cb1 = RPM ($cand - [uint64]5) 6
Say ('[PT] candidate 1 = module+0x471A983 (the real ret that ends lua_gettop); bytes at X-5 = ' + (HexOf $cb1 6) + '  (need e8......c3)')
if (CheckTramp $cand) {
  $script:trampX = $cand
  Say '[PT] candidate 1 ACCEPTED: lua_pcall will see a module return address with the E8 call shape'
} else {
  Say '[PT] candidate 1 rejected - scanning the first 1 MB of the Lua cluster for another E8..ret site'
  $scanBase = $script:base + [uint64]0x4700000
  $buf = RPM $scanBase 0x100000
  $found = 0
  if ($null -ne $buf) {
    $s = [System.Text.Encoding]::Latin1.GetString($buf)
    $pos = 0
    while ($true) {
      $i = $s.IndexOf([char]0xC3, $pos)
      if ($i -lt 0) { break }
      if ($i -ge 5 -and $s[$i - 5] -eq [char]0xE8) {
        $X2 = $scanBase + [uint64]$i
        if (CheckTramp $X2) { $script:trampX = $X2; $found = 1; break }
      }
      $pos = $i + 1
    }
  }
  if ($found -eq 1) { Say ('[PT] fallback ACCEPTED: module+0x' + ('{0:X}' -f ($script:trampX - $script:base))) }
  else { Say '[PT] no usable trampoline site - the bridge would be rejected again' }
}
if ($script:trampX -ne 0) { $script:trampMode = 1 }
Say ('[PT] trampoline mode = ' + $script:trampMode + '  X = 0x' + ('{0:X}' -f $script:trampX) + '  (module+0x' + ('{0:X}' -f ($script:trampX - $script:base)) + ')')
if ($PatchGuard) {
  Say '[GP] -PatchGuard requested: bypass the validator instead of satisfying it'
  [void](PatchGuard)
}
if ($script:trampMode -eq 1 -and $script:guardPatched) {
  $script:trampMode = 0
  Say '[PT] the validator is patched out, so the bridge uses mode 0: an ordinary `call lua_pcall`'
  Say '[PT] (the return address pushed by that call lives in our own page - only the patched validator would have minded)'
} elseif ($script:trampMode -eq 1) {
  Say '[PT] validator is live -> mode 1: two-hop return, rbp carries the continuation into the slot the ret at X pops'
}
if ($script:guardPatched) { Say '[PT] NOTE: if a call faults, the client dies a few seconds later - that delay is its crash handler, not a hang' }

# ---------------------------------------------------------------- bridge plumbing
function PutStr([uint64]$off, [string]$s) {
  $b = NewBytes ($s.Length + 1)
  [System.Text.Encoding]::ASCII.GetBytes($s).CopyTo($b, 0)
  $b[$s.Length] = 0
  return (WPM ($script:pageVA + $off) $b)
}
function ComposeBlock([uint64]$fnRva, [int]$namelen, [int]$arglen, [int]$nargs, [int]$nresults, [int]$variant, [bool]$skip, [uint64]$rawRd, [uint64]$rawR8) {
  $b = NewBytes 0x100
  PSet $b 0x00 $script:payloadVA
  PSet $b 0x08 $script:L
  PSet $b 0x10 ($script:pageVA + [uint64]0x700)
  if ($variant -eq 3) { PSet $b 0x18 $rawRd } else { PSet $b 0x18 ($script:pageVA + [uint64]0x740) }
  PSet $b 0x20 ([uint64]$nargs)
  PSet $b 0x28 ([uint64]$nresults)
  $sk = [uint64]0
  if ($skip) { $sk = [uint64]1 }
  PSet $b 0x30 $sk
  PSet $b 0x38 ([uint64]$arglen)
  PSet $b 0x40 ([uint64]$variant)
  PSet $b 0x48 ([uint64]$namelen)
  PSet $b 0x50 $rawR8
  PSet $b 0x58 $script:trampX
  PSet $b 0x60 ([uint64]$script:trampMode)
  PSet $b 0x78 ($script:base + [uint64]$fnRva)
  PSet $b 0x80 ($script:base + [uint64]0x471BC20)
  PSet $b 0x88 ($script:base + [uint64]0x471B5C0)
  PSet $b 0x98 ([uint64]0xDEAD)
  return , $b
}
function RenderCall([hashtable]$r, [int]$fx, [string]$what) {
  $s = '[CALL] ' + $what + ' : '
  if (-not $r.ok) {
    $s += 'did not complete (stage=0x' + ('{0:X}' -f $r.stage) + ' exit=0x' + ('{0:X}' -f $r.exit) + ' why=' + $r.why + ')'
    return $s
  }
  if ($fx -lt 2 -and $r.pcall -ne 0) {
    $s += 'pcall status=' + $r.pcall
    if ($r.tt1 -eq 4) { $s += '  error="' + (AsciiRun (RPM $r.v1 0x60)) + '"' }
    return $s
  }
  $tt = $r.tt1
  $v = $r.v1
  if ($fx -eq 2) { $tt = $r.tt2; $v = $r.v2 }
  if ($tt -eq 3) { $s += (F64B $r.blk (0xA0 + ($fx - 1) * 0x10)) }
  elseif ($tt -eq 4) { $s += (LuaStr $v) }
  elseif ($tt -eq 1) { if ($v -eq 0) { $s += 'false' } else { $s += 'true' } }
  elseif ($tt -eq 0) { $s += 'nil' }
  else { $s += ('<' + (TTName $tt) + ' 0x' + ('{0:X}' -f $v) + '>') }
  return $s
}
function BridgeOne([string]$tag, [uint64]$fnRva, [string]$name, [string]$arg, [int]$nargs, [int]$nresults, [int]$variant, [bool]$skip, [int]$timeoutMs, [uint64]$rawRd, [uint64]$rawR8) {
  $res = @{ ok = $false; stuck = $false; stage = [uint64]0; exit = [uint32]0; why = ''; pcall = -999; tt1 = -1; v1 = [uint64]0; tt2 = -1; v2 = [uint64]0; topB = [uint64]0; topA = [uint64]0; tid = 0; blk = $null }
  if (-not (PutStr 0x700 $name)) { $res.why = 'name write failed'; return $res }
  if ($nargs -gt 0 -and $variant -lt 2) { if (-not (PutStr 0x740 $arg)) { $res.why = 'arg write failed'; return $res } }
  $blk = ComposeBlock $fnRva $name.Length $arg.Length $nargs $nresults $variant $skip $rawRd $rawR8
  if (-not (WPM $script:ptrSlot $blk)) { $res.why = 'block write failed'; return $res }
  $back = RPM $script:ptrSlot 0x100
  if ($null -eq $back -or (HexOf $back 0x100) -ne (HexOf $blk 0x100)) { $res.why = 'block read-back mismatch'; return $res }
  $tid = [uint32]0
  $th = [H46]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$script:ptrSlot), 0, [ref]$tid)
  if ($th -eq [IntPtr]::Zero) { $res.why = ('CreateRemoteThread err=' + (GetLastErr)); return $res }
  $res.tid = $tid
  $script:lastTh = $th
  $w = [H46]::WaitForSingleObject($th, [uint32]$timeoutMs)
  $ec = [uint32]0
  [void][H46]::GetExitCodeThread($th, [ref]$ec)
  $res.exit = $ec
  if ($w -ne 258) { [void][H46]::CloseHandle($th) }   # a wedged thread keeps its handle (we sample it)
  $rb = RPM $script:ptrSlot 0x100
  if ($null -eq $rb) { $res.why = 'block unreadable after the call'; return $res }
  $res.blk = $rb
  $res.topB = U64B $rb 0x90
  $res.topA = U64B $rb 0x68
  $res.stage = U64B $rb 0x70
  $pv = U64B $rb 0x98
  $lo = [int64]($pv -band [int64]0xFFFFFFFF)
  if ($lo -gt 0x7FFFFFFF) { $lo = $lo - 0x100000000 }
  $res.pcall = [int]$lo
  $res.tt1 = [int](U16B $rb 0xA8)
  $res.v1 = U64B $rb 0xA0
  $res.tt2 = [int](U16B $rb 0xB8)
  $res.v2 = U64B $rb 0xB0
  if ($w -eq 258) {
    $res.stuck = $true
    $script:stuck = $true
    $res.why = 'STILL RUNNING'
    Say ('[' + $tag + '] tid=' + $tid + ' STILL RUNNING after ' + ($timeoutMs / 1000) + ' s  last stage=0x' + ('{0:X}' -f $res.stage) + ' exit=0x103')
    Say ('[' + $tag + '] the thread is left alone on purpose - TerminateThread is what killed the client in hunt40/hunt41,')
    Say ('[' + $tag + '] a fault inside the injected thread is parked by the client handler, which then kills the process a few seconds later')
    Say ('[' + $tag + '] (that delay is what looked like a hang in hunts 40-44). Sampling where it is:')
    WhereIs $th $tag
    return $res
  }
  $res.ok = ($res.stage -eq 0x4020)
  $exp = $res.topB
  if ($variant -lt 2) {
    if ($skip) { $exp = $res.topB + [uint64]0x18 } else { $exp = $res.topB + [uint64](0x18 * $nresults) }
  }
  if ($res.ok -and $res.topA -eq $exp) {
    [void](W64 ($script:L + [uint64]0x28) $res.topB)
  } elseif ($res.ok) {
    Say ('[' + $tag + '] WARNING: top_after=0x' + ('{0:X}' -f $res.topA) + ' but expected 0x' + ('{0:X}' -f $exp) + ' - not touching the stack')
  }
  return $res
}

# ---------------------------------------------------------------- [4] the resident bridge
Say ''
Say '== [4] resident bridge: ONE thread, a mailbox, many calls =='
Say '[M] the payload at page+0x800 polls a mailbox in our own page. The driver writes a'
Say '[M] command (CMD=1 + a new SEQ) and waits for the payload to copy SEQ into ACK, so a'
Say '[M] result is never read half-written. kernel32!GetTickCount64/Sleep are wired in, so'
Say '[M] the idle loop neither burns a core nor can outlive this script.'
$script:ctrlVA    = $script:pageVA + [uint64]0x1000
$script:ctrl2VA   = $script:pageVA + [uint64]0x1400
$script:mailSeq   = [uint64]0
$script:mailStuck = $false
$script:mailTh    = [IntPtr]::Zero
$script:mailOK    = $false
$script:tickFn    = [uint64]0
$script:sleepFn   = [uint64]0

function ResolveKernelFn([string]$fname) {
  # kernel32 is a KnownDLL, so every process on this boot maps it at the same address
  # and an address resolved here is valid in the client - but verify rather than trust
  $h = [H46]::GetModuleHandleA('kernel32.dll')
  if ($h -eq [IntPtr]::Zero) { return [uint64]0 }
  $p = [H46]::GetProcAddress($h, $fname)
  if ($p -eq [IntPtr]::Zero) { return [uint64]0 }
  $va = [uint64]$p.ToInt64()
  $loc = NewBytes 16
  [System.Runtime.InteropServices.Marshal]::Copy($p, $loc, 0, 16)
  $rem = RPM $va 16
  if ($null -eq $rem) { return [uint64]0 }
  if ((HexOf $loc 16) -ne (HexOf $rem 16)) { return [uint64]0 }
  $m = MBI $va
  if (($m.state -ne 0x1000) -or (($m.prot -band 0xF0) -eq 0)) { return [uint64]0 }
  return $va
}
function W32([uint64]$addr, [int]$v) {
  $b = NewBytes 4
  $t = [System.BitConverter]::GetBytes([int32]$v)
  for ($i = 0; $i -lt 4; $i++) { $b[$i] = $t[$i] }
  return (WPM $addr $b)
}
function MailInit([uint64]$ctrl, [int]$serveSec, [int]$sleepMs) {
  $ok = $true
  $t0 = [int64][H46]::GetTickCount64()
  $dl = [uint64]($t0 + [int64]($serveSec * 1000))
  $ok = (W64 ($ctrl + [uint64]0x00) $script:payloadVA) -and $ok   # [ctrl] is the gadget's jump target
  $ok = (W64 ($ctrl + [uint64]0x08) $script:L) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x10) ($ctrl + [uint64]0x200)) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x18) ($ctrl + [uint64]0x280)) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x58) $script:trampX) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x60) ([uint64]$script:trampMode)) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x78) ($script:base + [uint64]0x471A370)) -and $ok   # lua_getfield
  $ok = (W64 ($ctrl + [uint64]0x80) ($script:base + [uint64]0x471BC20)) -and $ok   # lua_pushlstring
  $ok = (W64 ($ctrl + [uint64]0x88) ($script:base + [uint64]0x471B5C0)) -and $ok   # lua_pcall
  $ok = (W64 ($ctrl + [uint64]0xF8) $script:tickFn) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x100) $script:sleepFn) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x108) $dl) -and $ok
  $ok = (W64 ($ctrl + [uint64]0x110) ([uint64]0x40000000)) -and $ok   # spin budget (fallback only)
  $ok = (W32 ($ctrl + [uint64]0x124) $sleepMs) -and $ok
  $ok = (W64 ($ctrl + [uint64]0xD0) ([uint64]0)) -and $ok
  $ok = (W64 ($ctrl + [uint64]0xD8) ([uint64]0)) -and $ok
  $ok = (W64 ($ctrl + [uint64]0xC8) ([uint64]0)) -and $ok
  return $ok
}
function MailStart([uint64]$ctrl, [int]$serveSec, [int]$sleepMs, [string]$tag) {
  if (-not (MailInit $ctrl $serveSec $sleepMs)) { Say ('[' + $tag + '] FATAL: the mailbox could not be filled'); return [IntPtr]::Zero }
  $tid = [uint32]0
  $th = [H46]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$ctrl), 0, [ref]$tid)
  if ($th -eq [IntPtr]::Zero) { Say ('[' + $tag + '] CreateRemoteThread failed err=' + (GetLastErr)); return [IntPtr]::Zero }
  Start-Sleep -Milliseconds 300
  $st = U64A ($ctrl + [uint64]0x70)
  $cmd = U64A ($ctrl + [uint64]0xC8)
  $ec = [uint32]0
  [void][H46]::GetExitCodeThread($th, [ref]$ec)
  Say ('[' + $tag + '] tid=' + $tid + ' stage=0x' + ('{0:X}' -f $st) + ' (' + (StageName46 $st) + ')  CMD=' + $cmd + '  exit=' + $ec + ' (259 = alive and idle)')
  return $th
}
function MailCmd([string]$tag, [string]$fname, [string]$arg, [int]$nargs, [int]$nresults, [int]$variant, [bool]$skip, [int]$timeoutMs, [uint64]$rawRd, [uint64]$rawR8, [uint64]$fnRva = 0) {
  $res = @{ ok = $false; why = ''; stage = [uint64]0; exit = [uint32]0; pcall = -999; ttRaw = -1; tt1 = -1; v1 = [uint64]0; tt2 = -1; v2 = [uint64]0; topB = [uint64]0; topA = [uint64]0; top1 = [uint64]0; calls = [uint64]0; ms = 0.0; blk = $null; topBad = $false }
  if ($script:mailStuck) { $res.why = 'a previous command is still running'; return $res }
  $c = $script:ctrlVA
  if (-not (PutStr 0x1200 $fname)) { $res.why = 'name write failed'; return $res }
  if ($variant -lt 2) {
    if ($nargs -gt 0) {
      if (-not (PutStr 0x1280 $arg)) { $res.why = 'arg write failed'; return $res }
    }
  }
  $sk = [uint64]0
  if ($skip) { $sk = [uint64]1 }
  [void](W64 ($c + [uint64]0x20) ([uint64]$nargs))
  [void](W64 ($c + [uint64]0x28) ([uint64]$nresults))
  [void](W64 ($c + [uint64]0x30) $sk)
  [void](W64 ($c + [uint64]0x38) ([uint64]$arg.Length))
  [void](W64 ($c + [uint64]0x40) ([uint64]$variant))
  [void](W64 ($c + [uint64]0x48) ([uint64]$fname.Length))
  [void](W64 ($c + [uint64]0x50) $rawR8)
  [void](W64 ($c + [uint64]0x98) ([uint64]0xDEAD))
  [void](W64 ($c + [uint64]0xA8) ([uint64]0xDEAD))
  [void](W64 ($c + [uint64]0xB8) ([uint64]0xDEAD))
  [void](W64 ($c + [uint64]0x70) ([uint64]0))
  $fa = [uint64]0x471A370
  if ($fnRva -ne 0) { $fa = $fnRva }
  [void](W64 ($c + [uint64]0x78) ($script:base + $fa))
  # +0x18 is the argument pointer for the bridge and the `nup` value for the factory,
  # so always set it explicitly instead of leaving one variant's value behind
  if ($variant -eq 3) { [void](W64 ($c + [uint64]0x18) $rawRd) }
  else { [void](W64 ($c + [uint64]0x18) ($c + [uint64]0x280)) }
  $script:mailSeq = $script:mailSeq + [uint64]1
  [void](W64 ($c + [uint64]0xD0) $script:mailSeq)
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  [void](W64 ($c + [uint64]0xC8) ([uint64]1))
  $got = $false
  while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
    if ((U64A ($c + [uint64]0xD8)) -eq $script:mailSeq) { $got = $true; break }
  }
  $sw.Stop()
  $res.ms = $sw.Elapsed.TotalMilliseconds
  $rb = RPM $c 0x100
  if ($null -eq $rb) { $res.why = 'control block unreadable'; return $res }
  $res.blk = $rb
  $res.stage = U64B $rb 0x70
  $res.topB = U64B $rb 0x90
  $res.topA = U64B $rb 0x68
  $res.top1 = U64B $rb 0xC0
  $res.calls = U64B $rb 0xE0
  $pv = U64B $rb 0x98
  if ($pv -eq [uint64]0xDEAD) { $res.pcall = -999 }
  else {
    $lo = [int64]($pv -band [int64]0xFFFFFFFF)
    if ($lo -gt 0x7FFFFFFF) { $lo = $lo - 0x100000000 }
    $res.pcall = [int]$lo
  }
  $tw1 = [int](U16B $rb 0xA8)
  $tw2 = [int](U16B $rb 0xB8)
  $res.ttRaw = $tw1
  if ($tw1 -ne 0xDEAD) { $res.tt1 = $tw1 -band 0xFF } else { $res.tt1 = -1 }
  if ($tw2 -ne 0xDEAD) { $res.tt2 = $tw2 -band 0xFF } else { $res.tt2 = -1 }
  $res.v1 = U64B $rb 0xA0
  $res.v2 = U64B $rb 0xB0
  if (-not $got) { $res.why = 'TIMEOUT'; $script:mailStuck = $true; return $res }
  $res.ok = ($res.stage -eq 0x4020)
  # stack discipline: lua_pcall pops func+args and pushes nresults
  $exp = $res.topB
  if ($variant -lt 2) {
    if ($skip) { $exp = $res.topB + [uint64]0x18 } else { $exp = $res.topB + [uint64](0x18 * $nresults) }
  }
  if ($res.ok) {
    if ($res.topA -eq $exp) { [void](W64 ($script:L + [uint64]0x28) $res.topB) }
    else {
      $res.topBad = $true
      Say ('[' + $tag + '] WARNING: top_after=0x' + ('{0:X}' -f $res.topA) + ' but expected 0x' + ('{0:X}' -f $exp) + ' - not touching the stack')
    }
  }
  return $res
}

$script:tickFn = ResolveKernelFn 'GetTickCount64'
$script:sleepFn = ResolveKernelFn 'Sleep'
$tickS = 'none (spin budget)'
if ($script:tickFn -ne 0) { $tickS = ('0x' + ('{0:X}' -f $script:tickFn)) }
$sleepS = 'none (pause spin)'
if ($script:sleepFn -ne 0) { $sleepS = ('0x' + ('{0:X}' -f $script:sleepFn)) }
Say ('[M] kernel32!GetTickCount64 = ' + $tickS + '   kernel32!Sleep = ' + $sleepS)
if ($script:stuck -or -not $script:payloadOk -or $SkipBridge -or $script:startSec -gt 4) {
  Say '[M] skipped'
} else {
  Say ('[M] control block = 0x' + ('{0:X}' -f $script:ctrlVA) + '   failsafe deadline = now + ' + $Serve + ' s   idle sleep = ' + $SleepMs + ' ms')
  $script:mailTh = MailStart $script:ctrlVA $Serve $SleepMs 'M'
  if ($script:mailTh -eq [IntPtr]::Zero) { Say '[M] VERDICT: the resident thread did not start' }
  elseif ((U64A ($script:ctrlVA + [uint64]0x70)) -eq 0x4100) { $script:mailOK = $true; Say '[M] VERDICT: the payload is resident and waiting for commands (stage 0x4100)' }
  else { Say '[M] VERDICT: the thread started but the payload is not in its idle loop - see the stage' }
}
Say ('[ALIVE] after [4]: ' + (Alive))

# ---------------------------------------------------------------- [5] first command
Say ''
Say '== [5] the first command through the mailbox: GetTime() =='
$ttFlag = 0
if ($script:mailTh -eq [IntPtr]::Zero -or $script:startSec -gt 5) {
  Say '[M1] skipped'
} else {
  $r = MailCmd 'M1' 'GetTime' '' 0 1 0 $false 8000 0 0
  Say ('[M1] stage=0x' + ('{0:X}' -f $r.stage) + ' (' + (StageName46 $r.stage) + ')  pcall=' + $r.pcall + '  latency=' + [math]::Round($r.ms,2) + ' ms  topB=0x' + ('{0:X}' -f $r.topB) + ' topA=0x' + ('{0:X}' -f $r.topA))
  Say ('[M1] ' + (RenderCall $r 1 'GetTime()'))
  if ((($r.ttRaw -band 0xFF00) -ne 0) -and $r.ttRaw -ne 0xDEAD) { $ttFlag = ($r.ttRaw -band 0xFF00); Say ('[M1] NOTE: the result type word is 0x' + ('{0:X}' -f $r.ttRaw) + ' - the 0x' + ('{0:X}' -f $ttFlag) + ' part is a value flag (secure/unit data), the type is tt & 0xFF') }
  if ($r.ok -and $r.pcall -eq 0 -and $r.tt1 -eq 3) {
    $script:mailOK = $true
    Say ('[M1] *** the mailbox works: GetTime() = ' + (F64B $r.blk 0xA0) + ' from a thread that was started once ***')
  } elseif ($r.why -ne '') { Say ('[M1] VERDICT: ' + $r.why + ' - the stage stamp says where the payload is') }
  else { Say '[M1] VERDICT: completed without a number - see the stage' }
}
Say ('[ALIVE] after [5]: ' + (Alive))

# ---------------------------------------------------------------- [6] the reader loop
Say ''
Say '== [6] reader loop: ' + $Iterations + ' rounds x 6 read-only calls, one resident thread =='
$roster = @(
  @{ f = 'GetTime'; a = ''; na = 0; nr = 1 },
  @{ f = 'UnitHealth'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitLevel'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitExists'; a = 'target'; na = 1; nr = 1 },
  @{ f = 'UnitGUID'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitName'; a = 'player'; na = 1; nr = 1 }
)
$lat = New-Object System.Collections.ArrayList
$script:sumCommands = 0
$script:sumFail = 0
$script:sumTopBad = 0
$script:sumAvg = 0.0
$script:sumPolls = 0.0
$script:sumCpu = 0.0
if ($script:stuck -or -not $script:mailOK -or $script:startSec -gt 6) {
  Say '[L] skipped: no command has completed through the mailbox yet'
} else {
  $gt1 = U64A ($script:G + [uint64]0x78)
  $gh1 = U64A ($script:G + [uint64]0x70)
  $tk1 = [int64]0; $tu1 = [int64]0; $tcr1 = [int64]0; $tex1 = [int64]0
  [void][H46]::GetThreadTimes($script:mailTh, [ref]$tcr1, [ref]$tex1, [ref]$tk1, [ref]$tu1)
  $swL = [System.Diagnostics.Stopwatch]::StartNew()
  for ($it = 1; $it -le $Iterations; $it++) {
    foreach ($c in $roster) {
      $r = MailCmd ('L' + $it) $c.f $c.a $c.na $c.nr 0 $false 8000 0 0
      $script:sumCommands++
      [void]$lat.Add($r.ms)
      if ($r.topBad) { $script:sumTopBad++ }
      if ($it -eq 1 -or $it -eq $Iterations) { Say ('[L' + $it + '] ' + (RenderCall $r 1 ($c.f + '(' + $c.a + ')'))) }
      if (-not ($r.ok -and $r.pcall -eq 0)) {
        $script:sumFail++
        Say ('[L] FAILURE at round ' + $it + ' on ' + $c.f + '(' + $c.a + '): stage=0x' + ('{0:X}' -f $r.stage) + ' pcall=' + $r.pcall + ' why=' + $r.why + ' after ' + [math]::Round($r.ms,2) + ' ms')
        break
      }
    }
    if ($script:sumFail -gt 0 -or $script:mailStuck) { break }
  }
  $swL.Stop()
  $tk2 = [int64]0; $tu2 = [int64]0; $tcr2 = [int64]0; $tex2 = [int64]0
  [void][H46]::GetThreadTimes($script:mailTh, [ref]$tcr2, [ref]$tex2, [ref]$tk2, [ref]$tu2)
  $cpuL = (($tk2 - $tk1) + ($tu2 - $tu1)) / 10000000.0
  $gt2 = U64A ($script:G + [uint64]0x78)
  $gh2 = U64A ($script:G + [uint64]0x70)
  if ($lat.Count -gt 0) {
    $srt = $lat | Sort-Object
    $avg = ($lat | Measure-Object -Average).Average
    $script:sumAvg = $avg
    Say ('[L] ' + $script:sumCommands + ' commands through ONE thread in ' + [math]::Round($swL.Elapsed.TotalSeconds,2) + ' s  ->  ' + [math]::Round($script:sumCommands / [math]::Max($swL.Elapsed.TotalSeconds, 0.001),1) + ' calls/s')
    Say ('[L] latency: min=' + [math]::Round($srt[0],2) + ' ms   p50=' + [math]::Round($srt[[int]($srt.Count / 2)],2) + ' ms   avg=' + [math]::Round($avg,2) + ' ms   max=' + [math]::Round($srt[$srt.Count - 1],2) + ' ms')
  }
  Say ('[L] failures=' + $script:sumFail + '  top_after mismatches=' + $script:sumTopBad + '  payload calls counter=' + (U64A ($script:ctrlVA + [uint64]0xE0)) + ' (expect ' + $script:sumCommands + ')')
  Say ('[L] payload thread CPU: ' + [math]::Round($cpuL,4) + ' s for ' + $script:sumCommands + ' commands = ' + [math]::Round((($cpuL * 1000000.0) / [math]::Max($script:sumCommands,1)),1) + ' us/command (the whole call, in the client)')
  $due = 'not due'
  if ($gt2 -ge $gh2) { $due = 'DUE' }
  Say ('[L] collector: totalbytes ' + $gt1 + ' -> ' + $gt2 + '   threshold ' + $gh1 + ' -> ' + $gh2 + '   -> ' + $due + '   (the bridge never forces a GC step: it always runs inside a real CallInfo)')
}
Say ('[ALIVE] after [6]: ' + (Alive))

# ---------------------------------------------------------------- [7] what idling costs
Say ''
Say '== [7] what idling costs (no commands for 2 s) =='
if ($script:mailOK -and -not $script:mailStuck) {
  $i1 = U64A ($script:ctrlVA + [uint64]0x118)
  $k1 = [int64]0; $u1 = [int64]0; $cr1 = [int64]0; $ex1 = [int64]0
  [void][H46]::GetThreadTimes($script:mailTh, [ref]$cr1, [ref]$ex1, [ref]$k1, [ref]$u1)
  $p1 = Get-Process -Id $script:gamePid -ErrorAction SilentlyContinue
  $c1 = [TimeSpan]::Zero
  if ($null -ne $p1) { $c1 = $p1.TotalProcessorTime }
  Start-Sleep -Milliseconds 2000
  $i2 = U64A ($script:ctrlVA + [uint64]0x118)
  $k2 = [int64]0; $u2 = [int64]0; $cr2 = [int64]0; $ex2 = [int64]0
  [void][H46]::GetThreadTimes($script:mailTh, [ref]$cr2, [ref]$ex2, [ref]$k2, [ref]$u2)
  $p2 = Get-Process -Id $script:gamePid -ErrorAction SilentlyContinue
  $cpuT = (($k2 - $k1) + ($u2 - $u1)) / 10000000.0
  $script:sumPolls = ($i2 - $i1) / 2.0
  $script:sumCpu = ($cpuT / 2.0) * 100.0
  Say ('[I] idles ' + $i1 + ' -> ' + $i2 + '  =  ' + [math]::Round($script:sumPolls,1) + ' polls/s')
  Say ('[I] the PAYLOAD THREAD (GetThreadTimes) used ' + [math]::Round($cpuT,4) + ' s of CPU over 2 s = ' + [math]::Round($script:sumCpu,2) + '% of one core')
  if ($null -ne $p2) {
    $dP = ($p2.TotalProcessorTime - $c1).TotalSeconds
    Say ('[I] (the whole client used ' + [math]::Round($dP,3) + ' s in the same window - that is the game itself, not us)')
  }
  if ($script:sleepFn -ne 0) { Say '[I] (the payload calls kernel32!Sleep between polls)' }
  else { Say '[I] (no Sleep resolved - the payload is pause-spinning, so a high number here is expected)' }
} else { Say '[I] skipped' }
Say ('[ALIVE] after [7]: ' + (Alive))

# ---------------------------------------------------------------- [8] shutdown + failsafe
Say ''
Say '== [8] the failsafe thread (2 s deadline, no driver) and clean shutdown =='
$script:sumFailsafe = $false
$script:sumShut = $false
if ($script:stuck -or -not $script:mailOK -or $script:startSec -gt 8) {
  Say '[F] skipped'
} else {
  $th2 = MailStart $script:ctrl2VA 2 $SleepMs 'F'
  if ($th2 -ne [IntPtr]::Zero) {
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $w2 = [H46]::WaitForSingleObject($th2, 12000)
    $sw2.Stop()
    $ec2 = [uint32]0
    [void][H46]::GetExitCodeThread($th2, [ref]$ec2)
    [void][H46]::CloseHandle($th2)
    $st2 = U64A ($script:ctrl2VA + [uint64]0x70)
    Say ('[F] unused failsafe thread: wait=' + $w2 + ' after ' + [math]::Round($sw2.Elapsed.TotalSeconds,2) + ' s  exit=0x' + ('{0:X}' -f $ec2) + '  stage=0x' + ('{0:X}' -f $st2) + ' (' + (StageName46 $st2) + ')')
    if ($w2 -eq 0 -and $ec2 -eq 0x4200CAFE) {
      $script:sumFailsafe = $true
      Say '[F] VERDICT: the payload exits on its own deadline - a driver that dies cannot leave a spinner in the client'
    } else { Say '[F] VERDICT: the failsafe did not fire as expected' }
  }
  if ($script:mailTh -ne [IntPtr]::Zero -and -not $script:mailStuck) {
    $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
    [void](W64 ($script:ctrlVA + [uint64]0xC8) ([uint64]0xFF))
    $w3 = [H46]::WaitForSingleObject($script:mailTh, 8000)
    $sw3.Stop()
    $ec3 = [uint32]0
    [void][H46]::GetExitCodeThread($script:mailTh, [ref]$ec3)
    [void][H46]::CloseHandle($script:mailTh)
    $st3 = U64A ($script:ctrlVA + [uint64]0x70)
    $cy = U64A ($script:ctrlVA + [uint64]0xE0)
    Say ('[X] exit command: wait=' + $w3 + ' after ' + [math]::Round($sw3.Elapsed.TotalSeconds,2) + ' s  exit=0x' + ('{0:X}' -f $ec3) + '  stage=0x' + ('{0:X}' -f $st3) + ' (' + (StageName46 $st3) + ')  it served ' + $cy + ' commands')
    if ($w3 -eq 0 -and $ec3 -eq 0x4200CAFE) {
      $script:sumShut = $true
      $script:mailTh = [IntPtr]::Zero
      Say '[X] VERDICT: the resident thread exited cleanly on command - no TerminateThread, no leak'
    } else { Say '[X] VERDICT: the thread did not exit as expected' }
  }
}
Say ('[ALIVE] after [8]: ' + (Alive))

# ---------------------------------------------------------------- [9] -Diag
if ($Diag) {
  Say ''
  Say '== [9] -Diag: allocation and the CClosure factory, through the resident mailbox =='
  if ($script:stuck -or -not $script:mailOK -or $script:startSec -gt 9) {
    Say '[D] skipped'
  } else {
    $fresh = 'zzq46' + [System.Guid]::NewGuid().ToString().Substring(0, 8)
    $d1 = MailCmd 'D1' $fresh '' 0 0 2 $false 8000 0 0 ([uint64]0x472BA70)
    Say ('[D1] luaS_newlstr(L,"' + $fresh + '") stage=0x' + ('{0:X}' -f $d1.stage) + ' ret=0x' + ('{0:X}' -f $d1.v1) + ' latency=' + [math]::Round($d1.ms,2) + ' ms')
    if ($d1.ok) { Say '[D1] VERDICT: interning a brand-new string allocates fine from the resident thread' }
    $env = [uint64]0
    $gtb = RPM ($script:L + [uint64]0x90) 8
    if ($null -ne $gtb) { $env = U64B $gtb 0 }
    $d2 = MailCmd 'D2' '' '' 0 0 3 $false 8000 ([uint64]0) $env ([uint64]0x4720B50)
    Say ('[D2] CClosure factory(L, nup=0, env=0x' + ('{0:X}' -f $env) + ') stage=0x' + ('{0:X}' -f $d2.stage) + ' ret=0x' + ('{0:X}' -f $d2.v1) + ' latency=' + [math]::Round($d2.ms,2) + ' ms')
    if ($d2.ok) { Say '[D2] VERDICT: the factory returns - so pushcclosure''s old wedge was around it (the GC branch), not in it' }
    elseif ($d2.why -eq 'TIMEOUT') { Say '[D2] VERDICT: the factory still wedges from a foreign thread' }
  }
  Say ('[ALIVE] after [9]: ' + (Alive))
}

# ---------------------------------------------------------------- [10] summary
Say ''
Say '== [10] summary =='
Say ('[SUM] resident=' + $script:mailOK + '  commands=' + $script:sumCommands + '  failures=' + $script:sumFail + '  avgLatency=' + [math]::Round($script:sumAvg,2) + ' ms  polls/s=' + [math]::Round($script:sumPolls,1) + '  clientCPU=' + [math]::Round($script:sumCpu,2) + '%  failsafe=' + $script:sumFailsafe + '  cleanShutdown=' + $script:sumShut + '  stuck=' + $script:mailStuck)
if ($script:mailOK -and $script:sumFail -eq 0 -and $script:sumShut) {
  Say '[SUM] the bridge is ready for the C# side: fill the block, set CMD=1, wait for ACK, read the results'
  Say '[SUM] the same protocol works from the future executor - it only has to write commands into its own page'
}
if ($script:mailStuck) {
  Say '[SUM] a command did not complete - read its stage stamp and the [M]/[L] lines; do not reuse the client, restart it'
}
UnpatchGuard
if ($script:pageVA -ne 0) {
  if ($script:mailStuck -or $script:mailTh -ne [IntPtr]::Zero) {
    Say '[CLEAN] page NOT freed - a payload thread may still be live inside it (freeing it under the thread is what killed the client after hunt43)'
  } else {
    $fr = [H46]::VirtualFreeEx($script:hProc, [IntPtr]([int64]$script:pageVA), [IntPtr]::Zero, 0x8000)
    Say ('[CLEAN] page free=' + $fr + ' (safe: every payload thread exited)')
  }
}
Say '[DONE] upload C:\ghidra-scripts\hunt46-out.txt'
