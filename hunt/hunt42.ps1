# hunt42.ps1 - the allocation-free bridge: get the real closure out of the globals table
# ---------------------------------------------------------------------------
# WHAT THE hunt41 RUN PROVED (its log only said "hunt40" in the header - stale text):
#   [0] every callee, including the never-before-probed factory page 0x4720000, is
#       WARM and READABLE -> cold-code was never the problem.
#   [4] lua_pushcclosure wedged AGAIN, with G+0x78 forced to 0 and the collector not
#       due -> the wedge is inside pushcclosure's own body, not the collector.
#   [4b] never ran (the stuck thread hard-stopped the script) and the client died -
#       the TerminateThread dropped it from 72 threads to 1.  Termination is what
#       killed the client, exactly like hunt40.  So hunt42 NEVER terminates.
#
# WHY pushcclosure WEDGES (read off the live dump, module-relative):
#   0x471B800 lua_pushcclosure(L, f, nup):
#     GC check -> 0x47190C0 getcurrenv(L) -> 0x4720B50 factory(L, nup, env)
#   0x4720B50: size = 0x38 + nup*24; 0x472FB50 luaM_realloc_(L, NULL, 0, size);
#              call qword [G+0x10]  <-- G->frealloc, ud = [G+0x18]
#              (0x472FB50 is the only allocator entry; it also does
#               'add [G+0x78], size - oldsize', i.e. G+0x78 = totalbytes and
#               G+0x70 = GCthreshold - hunt40/41 logged those two the other way round)
#   0x472D770 luaC_link: obj->next = G->rootgc; obj+0x10 = tt; obj+0x11 = white bits
#              (the CClosure's f lands at closure+0x30, isC at +0x12, nup at +0x13)
#   -> hunt41's [2b] closure scan tested the type byte at hit-0x28 == closure+0x08,
#      but the object's tt byte is at +0x10 - that is why it found 0 candidates.
#
# THE FIX: the bridge does not need pushcclosure at all.
#   0x471A370 lua_getfield(L, idx, k)   (3-arg, NUL-terminated; 0x471A500 is the
#                                       length-taking twin, (L, idx, s, len))
#   index2adr(L, -10002) returns &L->l_gt (L+0x90) - the globals table.
#   getfield + lua_pushlstring("player") + lua_pcall(L,1,1,0) allocates NOTHING:
#   both names were interned by the client at startup, so luaS_newlstr just returns
#   the existing TString.
#
# hunt42 layout: page+0x000 scratch/pattern, +0x100 fake state (the gettop proof),
# +0x300 the 379-byte generic bridge payload (assembled offline with keystone and
# disassembled again to verify - no RIP-relative operands, everything comes from the
# parameter block), +0x600 the block (also the gadget's pointer slot), +0x700 the
# function name, +0x740 the argument.
#
# The payload takes rcx = &block (the gadget `jmp qword ptr [rcx]` passes the thread
# parameter straight through) and reads L, the callee addresses and the strings from
# the block - so the code itself is constant and every call is one block write.
#   stage 0x4001 entered      0x4002 getfield returned   0x4003 before pushlstring
#   0x4004 before pcall       0x4005 results captured    0x4020 COMPLETE
#   0x4010/0x4011 raw call (-Diag).  Thread exit code 0x4200CAFE = ran to the end.
#
# ---------------------------------------------------------------------------
param([switch]$Diag, [switch]$SkipBridge, [switch]$ForceExec)


$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt41-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function GetLastErr { return [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H42 {
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
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool TerminateThread(IntPtr h, uint code);
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
  if (-not [H42]::ReadProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)) { return $null }
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
  if (-not [H42]::WriteProcessMemory($script:hProc, [IntPtr]([int64]$addr), $buf, $buf.Length, [ref]$wrote)) { return $false }
  return ($wrote.ToInt64() -eq $buf.Length)
}
function W64([uint64]$addr, [uint64]$v) { return (WPM $addr ([System.BitConverter]::GetBytes($v))) }
function MBI([uint64]$addr) {
  $buf = NewBytes 0x30
  $r = [H42]::VirtualQueryEx($script:hProc, [IntPtr]([int64]$addr), $buf, [uint64]0x30)
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
  $p = [H42]::VirtualAlloc([IntPtr]::Zero, [IntPtr]1232, 0x3000, 0x04)
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


# ---------------------------------------------------------------- attach + canary
Say '[HDR] hunt42 - the allocation-free bridge: globals lookup + pushlstring + pcall'
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the client and rerun'; exit 1 }
if ($proc -is [array]) { Say ('[HDR] WARNING: ' + $proc.Count + ' WowB processes; using the first'); $proc = $proc[0] }
$script:gamePid = $proc.Id
$script:hProc = [H42]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H42]::OpenProcess(0x043A, $false, $proc.Id) }
if ($script:hProc -eq [IntPtr]::Zero) { $script:hProc = [H42]::OpenProcess(0x0418, $false, $proc.Id) }
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
  $fake = [H42]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
  if ($fake -eq [IntPtr]::Zero) {
    Say ('[P1] VirtualAllocEx failed err=' + (GetLastErr))
  } else {
    if (-not ((W64 ($fake + [uint64]0x28) ($fake + [uint64]0xC88)) -and (W64 ($fake + [uint64]0x30) $fake))) {
      Say '[P1] could not write the fake state'
    } else {
      $tid1 = [uint32]0
      $th1 = [H42]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + [uint64]0x471A960)), [IntPtr]([int64]$fake), 0, [ref]$tid1)
      if ($th1 -eq [IntPtr]::Zero) {
        Say ('[P1] CreateRemoteThread failed err=' + (GetLastErr))
      } else {
        $w1 = [H42]::WaitForSingleObject($th1, 5000)
        $ec1 = [uint32]0
        [void][H42]::GetExitCodeThread($th1, [ref]$ec1)
        [void][H42]::CloseHandle($th1)
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
$pageVA2 = [H42]::VirtualAllocEx($script:hProc, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
if ($pageVA2 -eq [IntPtr]::Zero) { Say ('[PG] VirtualAllocEx failed err=' + (GetLastErr)); exit 1 }
$script:pageVA = [uint64]$pageVA2.ToInt64()
$script:ptrSlot = $script:pageVA + [uint64]0x600
$script:payloadVA = $script:pageVA + [uint64]0x300
Say ('[PG] page=0x' + ('{0:X}' -f $script:pageVA) + '  block=0x' + ('{0:X}' -f $script:ptrSlot) + '  payload=0x' + ('{0:X}' -f $script:payloadVA))
$pat = NewBytes 16
for ($i = 0; $i -lt 16; $i++) { $pat[$i] = [byte](0x40 + $i) }
[void](WPM ($script:pageVA + [uint64]0x40) $pat)
$r1 = RPM ($script:pageVA + [uint64]0x40) 16
Say ('[PG] pattern read-back = ' + (HexOf $r1 16) + ' match=' + ((HexOf $r1 16) -eq (HexOf $pat 16)))
$exitStub = HexToBytes '48c70545fdffff00300000b844332211c3'
[void](WPM $script:payloadVA $exitStub)
[void](W64 $script:ptrSlot $script:payloadVA)
$bk3 = RPM $script:payloadVA $exitStub.Length
if ($null -eq $bk3 -or (HexOf $bk3 $exitStub.Length) -ne (HexOf $exitStub $exitStub.Length)) {
  Say '[G3] ABORT: the exit stub did not read back'
} elseif ($gadgetRva -eq 0) {
  Say '[G3] skipped: no gadget'
} else {
  $tid3 = [uint32]0
  $th3 = [H42]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$script:ptrSlot), 0, [ref]$tid3)
  if ($th3 -eq [IntPtr]::Zero) {
    Say ('[G3] CreateRemoteThread failed err=' + (GetLastErr))
  } else {
    $w3 = [H42]::WaitForSingleObject($th3, 5000)
    $ec3 = [uint32]0
    [void][H42]::GetExitCodeThread($th3, [ref]$ec3)
    [void][H42]::CloseHandle($th3)
    $st3 = U64A ($script:pageVA + [uint64]0x50)
    Say ('[G3] tid=' + $tid3 + ' wait=' + $w3 + ' exit=0x' + ('{0:X}' -f $ec3) + ' stage=0x' + ('{0:X}' -f $st3))
    if ($ec3 -eq 0x11223344 -and $st3 -eq 0x3000) { $gadgetProof = $true; Say '[G3] VERDICT: module-start thread -> our page. The gadget vector WORKS.' }
    else { Say '[G3] VERDICT: our code did not run through the gadget' }
  }
}
$script:payloadOk = $false
if ($gadgetProof) {
  $pl = HexToBytes '4889cb4883ec284c8b73084d8b7e284c89bb9000000048c743700140000048c783c00000000000000048837b40020f83f40000004c89f1baeed8ffff4c8b431048837b40017408488b4378ffd0eb0a4c8b4b48488b4378ffd048c74370024000004d8b6e284c89abc0000000498b45e8488983a0000000410fb745f0668983a800000048837b30000f85d300000048c743700340000048837b200074144c89f1488b53184c8b4338488b8380000000ffd048c74370044000004c89f1488b53204c8b43284531c9488b8388000000ffd04889839800000048c74370054000004d8b6e284c89abc000000048837b28017c70498b45e8488983a0000000410fb745f0668983a800000048837b28027c52498b45d0488983b0000000410fb745d8668983b8000000eb3948c74370104000004c89f148837b4003740a488b53104c8b4348eb08488b53184c8b4350488b4378ffd0488983a000000048c7437011400000498b46284889436848c74370204000004883c428b8feca0042c3'
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
  $th = [H42]::CreateRemoteThread($script:hProc, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]([int64]($script:base + $gadgetRva)), [IntPtr]([int64]$script:ptrSlot), 0, [ref]$tid)
  if ($th -eq [IntPtr]::Zero) { $res.why = ('CreateRemoteThread err=' + (GetLastErr)); return $res }
  $res.tid = $tid
  $w = [H42]::WaitForSingleObject($th, [uint32]$timeoutMs)
  $ec = [uint32]0
  [void][H42]::GetExitCodeThread($th, [ref]$ec)
  [void][H42]::CloseHandle($th)
  $res.exit = $ec
  $rb = RPM $script:ptrSlot 0x100
  if ($null -eq $rb) { $res.why = 'block unreadable after the call'; return $res }
  $res.blk = $rb
  $res.topB = U64B $rb 0x90
  $res.topA = U64B $rb 0x68
  $res.stage = U64B $rb 0x70
  $pv = U64B $rb 0x98
  $res.pcall = [int]($pv -band [uint64]0xFFFFFFFF)
  $res.tt1 = [int](U16B $rb 0xA8)
  $res.v1 = U64B $rb 0xA0
  $res.tt2 = [int](U16B $rb 0xB8)
  $res.v2 = U64B $rb 0xB0
  if ($w -eq 258) {
    $res.stuck = $true
    $script:stuck = $true
    $res.why = 'STILL RUNNING'
    Say ('[' + $tag + '] tid=' + $tid + ' STILL RUNNING after ' + ($timeoutMs / 1000) + ' s  last stage=0x' + ('{0:X}' -f $res.stage) + ' exit=0x103')
    Say ('[' + $tag + '] the thread is left alone on purpose - TerminateThread is what killed the client in hunt40/hunt41')
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

# ---------------------------------------------------------------- [4] getfield probe
Say ''
Say '== [4] PROBE: lua_getfield alone - does the globals lookup work from our thread? =='
$probeOk = $false
if (-not $script:payloadOk) {
  Say '[A] skipped: no payload'
} else {
  $a1 = BridgeOne 'A1' 0x471A370 'UnitHealth' '' 0 0 0 $true 6000 0 0
  Say ('[A1] exit=0x' + ('{0:X}' -f $a1.exit) + ' stage=0x' + ('{0:X}' -f $a1.stage) + '  tt=' + $a1.tt1 + ' (' + (TTName $a1.tt1) + ') value=0x' + ('{0:X}' -f $a1.v1) + '  topB=0x' + ('{0:X}' -f $a1.topB))
  Say ('[A1] why=' + $a1.why)
  if ($a1.stage -eq 0x4020 -and $a1.tt1 -eq 6) {
    $probeOk = $true
    Say '[A1] VERDICT: the globals lookup WORKS - the real CClosure pointer is on the stack (and the stack was restored)'
  } elseif ($a1.stuck) {
    Say '[A1] VERDICT: the LOOKUP wedged - read the stage: 0x4001 = never returned from getfield'
  } elseif ($a1.stage -eq 0x4020) {
    Say ('[A1] VERDICT: getfield returned but pushed ' + (TTName $a1.tt1) + ' instead of a function - wrong name or wrong function entry')
  } else {
    Say '[A1] VERDICT: stopped without completing - see the stage'
  }
  if (-not $script:stuck) {
    $a2 = BridgeOne 'A2' 0x471A500 'UnitHealth' '' 0 0 1 $true 6000 0 0
    Say ('[A2] (length-taking twin) stage=0x' + ('{0:X}' -f $a2.stage) + ' tt=' + $a2.tt1 + ' (' + (TTName $a2.tt1) + ') value=0x' + ('{0:X}' -f $a2.v1))
  }
  if (-not $script:stuck) {
    $a3 = BridgeOne 'A3' 0x471A370 'GetTime' '' 0 0 0 $true 6000 0 0
    Say ('[A3] (a second name)    stage=0x' + ('{0:X}' -f $a3.stage) + ' tt=' + $a3.tt1 + ' (' + (TTName $a3.tt1) + ')')
  }
}
Say ('[ALIVE] after [4]: ' + (Alive))

# ---------------------------------------------------------------- [5] the bridge
Say ''
Say '== [5] BRIDGE: UnitHealth("player") = getfield + pushlstring + pcall =='
$bridgeOk = $false
if ($script:stuck -or -not $script:payloadOk -or $SkipBridge) {
  Say '[B1] skipped'
} else {
  $b1 = BridgeOne 'B1' 0x471A370 'UnitHealth' 'player' 1 1 0 $false 6000 0 0
  Say ('[B1] exit=0x' + ('{0:X}' -f $b1.exit) + ' stage=0x' + ('{0:X}' -f $b1.stage) + ' pcall=' + $b1.pcall + ' -> ' + (RenderCall $b1 1 'UnitHealth("player")'))
  if ($b1.ok -and $b1.pcall -eq 0) {
    $bridgeOk = $true
    if ($b1.tt1 -eq 3) { Say ('[B1] *** UnitHealth("player") = ' + (F64B $b1.blk 0xA0) + '  <- the x64 bridge works end to end ***') }
    else { Say ('[B1] the call returned ' + (TTName $b1.tt1) + ' - not a number, but the bridge itself ran') }
  } elseif ($b1.stuck) {
    Say '[B1] VERDICT: wedged inside the call - the stage names the step'
  } else {
    Say '[B1] VERDICT: did not complete'
  }
}
Say ('[ALIVE] after [5]: ' + (Alive))

# ---------------------------------------------------------------- [6] snapshot
Say ''
Say '== [6] read-only snapshot through the same bridge (one thread each) =='
$snapOk = 0
$calls = @(
  @{ f = 'UnitHealth'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitExists'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitHealth'; a = 'target'; na = 1; nr = 1 },
  @{ f = 'UnitExists'; a = 'target'; na = 1; nr = 1 },
  @{ f = 'UnitLevel'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'GetTime'; a = ''; na = 0; nr = 1 },
  @{ f = 'GetMoney'; a = ''; na = 0; nr = 1 },
  @{ f = 'IsLoggedIn'; a = ''; na = 0; nr = 1 },
  @{ f = 'UnitClass'; a = 'player'; na = 1; nr = 2 },
  @{ f = 'UnitName'; a = 'player'; na = 1; nr = 1 },
  @{ f = 'UnitGUID'; a = 'player'; na = 1; nr = 1 }
)
if ($script:stuck -or -not $bridgeOk) {
  Say '[S] skipped: the bridge is not confirmed'
} else {
  $i = 0
  foreach ($c in $calls) {
    $i++
    if ($script:stuck) { Say ('[S' + $i + '] skipped - a payload thread is wedged, restart the client'); break }
    $what = $c.f + '(' + $c.a + ')'
    $r = BridgeOne ('S' + $i) 0x471A370 $c.f $c.a $c.na $c.nr 0 $false 6000 0 0
    Say (RenderCall $r 1 ('S' + $i + ' ' + $what))
    if ($c.nr -eq 2) { Say ('[S' + $i + ']   second result: ' + (RenderCall $r 2 $what)) }
    if ($r.ok -and $r.pcall -eq 0) { $snapOk++ }
  }
}
Say ('[ALIVE] after [6]: ' + (Alive))

# ---------------------------------------------------------------- [7] -Diag allocation probes
if ($Diag) {
  Say ''
  Say '== [7] -Diag: raw allocation probes (these settle WHY pushcclosure wedges) =='
  if ($script:stuck -or -not $script:payloadOk) {
    Say '[D] skipped'
  } else {
    $fresh = 'zzprobe42' + [System.Guid]::NewGuid().ToString().Substring(0, 8)
    $d1 = BridgeOne 'D1' 0x472BA70 $fresh '' 0 0 2 $false 6000 0 0
    Say ('[D1] luaS_newlstr(L,"' + $fresh + '",len) stage=0x' + ('{0:X}' -f $d1.stage) + ' ret=0x' + ('{0:X}' -f $d1.v1) + ' why=' + $d1.why)
    if ($d1.ok) { Say '[D1] VERDICT: interning a NEW string allocates fine from our thread - allocation is not the blocker' }
    elseif ($d1.stuck) { Say '[D1] VERDICT: ALLOCATION WEDGES from a foreign thread - that is the pushcclosure wedge, and everything must go through the getfield path (which allocates nothing)' }
  }
  if (-not $script:stuck -and $script:payloadOk) {
    $env = [uint64]0
    $gtb = RPM ($script:L + [uint64]0x90) 8
    if ($null -ne $gtb) { $env = U64B $gtb 0 }
    $d2 = BridgeOne 'D2' 0x4720B50 '' '' 0 0 3 $false 6000 ([uint64]0) $env
    Say ('[D2] CClosure factory(L, nup=0, env=0x' + ('{0:X}' -f $env) + ') stage=0x' + ('{0:X}' -f $d2.stage) + ' ret=0x' + ('{0:X}' -f $d2.v1) + ' why=' + $d2.why)
    if ($d2.ok) { Say '[D2] VERDICT: the factory itself returns - so pushcclosure wedges around it (the GC check or getcurrenv)' }
    elseif ($d2.stuck) { Say '[D2] VERDICT: the factory wedges -> the ALLOCATOR is the wedge' }
  }
  Say ('[ALIVE] after [7]: ' + (Alive))
}

# ---------------------------------------------------------------- [8] summary
Say ''
Say '== [8] summary =='
Say ('[SUM] execOk=' + $script:execOk + '  gadgetProof=' + $gadgetProof + '  payloadOk=' + $script:payloadOk + '  getfieldProbe=' + $probeOk + '  bridgeOk=' + $bridgeOk + '  snapshotOk=' + $snapOk + '/' + $calls.Count + '  stuck=' + $script:stuck)
if ($script:stuck) { Say '[SUM] a payload thread WEDGED - the log above ends at the last stage stamp; restart the client and rerun' }
if ($bridgeOk) { Say '[SUM] next: hand this to the reader - the same bridge can call any read-only API (GetUnitField, UnitGUID, GetTime, ...) and returns numbers and strings' }
if ($script:pageVA -ne 0) {
  $fr = [H42]::VirtualFreeEx($script:hProc, [IntPtr]([int64]$script:pageVA), [IntPtr]::Zero, 0x8000)
  Say ('[CLEAN] page free=' + $fr)
}
Say '[DONE] upload C:\ghidra-scripts\hunt42-out.txt'
