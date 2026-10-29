# hunt36.ps1 - are the memory APIs lying to us?  (read-only honesty probe)
# ---------------------------------------------------------------------------
# hunt35's log cannot be true: the game process was alive with 73 threads, yet
#   * our OWN freshly written page read back as 16 zero bytes
#     ([RT] stub readback = 0000...0000, right after a WPM that reported 560
#      bytes written - the script should have ABORTED there, that is the bug
#      that let [2] point thread RIPs at an unverified page),
#   * every readable module page (lua_pcall, UnitHealth, ...) read as zeros
#     while six pages failed outright,
#   * VirtualQueryEx reported Protect=0x0 for ALL of them - including pages we
#     know are NOACCESS, whose reads do fail.
# A live process cannot have zeros in its PE header (base+0 must be 4D 5A 90 00)
# and cannot have a 0-length MBI protection.  So either something intercepts
# foreign memory access to WowB (anti-tamper driver / API hooks), or the client
# was mid-crash.  This script decides which, without executing anything in the
# target: no thread creation, no context changes, no protection changes, and a
# write only to a page WE allocate (skip it with -NoWrite).
#
# [0] inventory (pid, start time, module base/size, harness processes, children)
# [1] read honesty matrix: kernel32!ReadProcessMemory vs ntdll!NtReadVirtualMemory
#     on  wowb base+0 (MZ header) | wowb base+0x471B5C0 | wowb base+0x7B76AB8
#         this process' own module base | a control process' module base
# [2] VirtualQueryEx matrix over the same addresses (full MBI fields)
# [3] write honesty: WPM a pattern into a page WE allocate, read it back via
#     both APIs, re-read after 1.5 s (catches a "zero the shellcode" scanner)
# [4] protection inventory: non-Windows modules in WowB, anti-cheat-shaped
#     processes, running non-system kernel drivers
# [5] verdicts
#
# RUN: elevated PowerShell, WowB.exe running (whatever state it is in).
# LOG: C:\ghidra-scripts\hunt36-out.txt
# ---------------------------------------------------------------------------
param([switch]$NoWrite)

$logDir  = 'C:\ghidra-scripts'
$logPath = Join-Path $logDir 'hunt36-out.txt'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
Set-Content -Path $logPath -Value '' -Encoding UTF8

function Say([string]$m) { Write-Host $m; Add-Content -Path $logPath -Value $m -Encoding UTF8 }
function GetLastErr { return [Runtime.InteropServices.Marshal]::GetLastWin32Error() }

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class H36 {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool b, int pid);
  [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr got);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] buf, int n, ref IntPtr wrote);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, IntPtr sz, uint alloc, uint prot);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFreeEx(IntPtr h, IntPtr a, IntPtr sz, uint f);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern ulong VirtualQueryEx(IntPtr h, IntPtr a, byte[] buf, ulong len);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
  [DllImport("ntdll.dll")] public static extern int NtReadVirtualMemory(IntPtr h, IntPtr a, byte[] buf, IntPtr n, out IntPtr got);
  [DllImport("ntdll.dll")] public static extern int NtWriteVirtualMemory(IntPtr h, IntPtr a, byte[] buf, IntPtr n, out IntPtr wrote);
}
'@

# ---------------------------------------------------------------- helpers
$script:hWowb = [IntPtr]::Zero
$script:hSelf = [H36]::GetCurrentProcess()
$script:hCtrl = [IntPtr]::Zero
$script:gamePid = 0
$script:base = [uint64]0

function NewBytes([int]$n) { return [byte[]]::new($n) }
function HexOf([byte[]]$b, [int]$n) {
  if ($null -eq $b) { return 'none' }
  $k = [Math]::Min($n, $b.Length)
  $s = ''
  for ($i = 0; $i -lt $k; $i++) { $s += ('{0:x2}' -f $b[$i]) }
  return $s
}
function ZeroOf([byte[]]$b, [int]$n) {
  if ($null -eq $b) { return $true }
  $k = [Math]::Min($n, $b.Length)
  for ($i = 0; $i -lt $k; $i++) { if ($b[$i] -ne 0) { return $false } }
  return $true
}

# --- kernel32 path
function K32Read([IntPtr]$h, [uint64]$addr, [int]$n) {
  $buf = NewBytes $n
  $got = [IntPtr]::Zero
  $ok = [H36]::ReadProcessMemory($h, [IntPtr]([int64]$addr), $buf, $n, [ref]$got)
  return @{ ok = $ok; got = $got.ToInt64(); err = (GetLastErr); bytes = $buf }
}
function K32Write([IntPtr]$h, [uint64]$addr, [byte[]]$data) {
  $wrote = [IntPtr]::Zero
  $ok = [H36]::WriteProcessMemory($h, [IntPtr]([int64]$addr), $data, $data.Length, [ref]$wrote)
  return @{ ok = $ok; got = $wrote.ToInt64(); err = (GetLastErr) }
}
# --- ntdll path (NTSTATUS: 0 = success)
function NtRead([IntPtr]$h, [uint64]$addr, [int]$n) {
  $buf = NewBytes $n
  $got = [IntPtr]::Zero
  $st = [H36]::NtReadVirtualMemory($h, [IntPtr]([int64]$addr), $buf, [IntPtr]$n, [ref]$got)
  return @{ ok = ($st -eq 0); got = $got.ToInt64(); st = $st; bytes = $buf }
}
function NtWrite([IntPtr]$h, [uint64]$addr, [byte[]]$data) {
  $wrote = [IntPtr]::Zero
  $st = [H36]::NtWriteVirtualMemory($h, [IntPtr]([int64]$addr), $data, [IntPtr]$data.Length, [ref]$wrote)
  return @{ ok = ($st -eq 0); got = $wrote.ToInt64(); st = $st }
}
function MBI([IntPtr]$h, [uint64]$addr) {
  $buf = NewBytes 0x30
  $r = [H36]::VirtualQueryEx($h, [IntPtr]([int64]$addr), $buf, [uint64]0x30)
  $o = [ordered]@{ call = $r; base = 0; allocbase = 0; allocprot = 0; size = 0; state = 0; prot = 0; type = 0 }
  if ($r -ge 0x30) {
    $o.base = [System.BitConverter]::ToUInt64($buf, 0x00)
    $o.allocbase = [System.BitConverter]::ToUInt64($buf, 0x08)
    $o.allocprot = [System.BitConverter]::ToUInt32($buf, 0x10)
    $o.size = [System.BitConverter]::ToUInt64($buf, 0x18)
    $o.state = [System.BitConverter]::ToUInt32($buf, 0x20)
    $o.prot = [System.BitConverter]::ToUInt32($buf, 0x24)
    $o.type = [System.BitConverter]::ToUInt32($buf, 0x28)
  }
  return $o
}
function MbiLine([string]$tag, [IntPtr]$h, [uint64]$addr) {
  $m = MBI $h $addr
  Say ('[MBI-' + $tag + '] 0x' + ('{0:X}' -f $addr) + ' ret=0x' + ('{0:X}' -f [uint64]$m.call) + ' base=0x' + ('{0:X}' -f $m.base) + ' size=0x' + ('{0:X}' -f $m.size) + ' state=0x' + ('{0:X}' -f $m.state) + ' prot=0x' + ('{0:X}' -f $m.prot) + ' type=0x' + ('{0:X}' -f $m.type))
}

# ---------------------------------------------------------------- [0] inventory
Say '[HDR] hunt36 - memory-API honesty probe (read-only)'
$proc = Get-Process -Name WowB -ErrorAction SilentlyContinue
if ($null -eq $proc) { Say '[HDR] WowB.exe not running - start the client and rerun'; exit 1 }
if ($proc -is [array]) { Say ('[HDR] WARNING: ' + $proc.Count + ' WowB processes; using the first'); $proc = $proc[0] }
$script:gamePid = $proc.Id
Say ('[HDR] WowB pid=' + $proc.Id + ' started=' + $proc.StartTime + ' threads=' + $proc.Threads.Count)
$script:base = [uint64]$proc.MainModule.BaseAddress.ToInt64()
Say ('[HDR] module base=0x' + ('{0:X}' -f $script:base) + ' size=0x' + ('{0:X}' -f [uint64]$proc.MainModule.ModuleMemorySize))
Say ('[HDR] every live PE must have 4D 5A 90 00 at base+0 - that is the control that decides whether reads are real')

$script:hWowb = [H36]::OpenProcess(0x1F0FFF, $false, $proc.Id)
if ($script:hWowb -eq [IntPtr]::Zero) { $script:hWowb = [H36]::OpenProcess(0x0418, $false, $proc.Id) }
if ($script:hWowb -eq [IntPtr]::Zero) { Say ('[HDR] OpenProcess(WowB) failed err=' + (GetLastErr)); exit 1 }
Say '[HDR] OpenProcess(WowB) ok'

# a control process (must read as a healthy PE)
$ctrl = $null
foreach ($nm in @('explorer', 'svchost', 'RuntimeBroker', 'SearchHost')) {
  $c = Get-Process -Name $nm -ErrorAction SilentlyContinue
  if ($null -ne $c -and -not ($c -is [array])) { $ctrl = $c; break }
  if ($null -ne $c -and $c -is [array]) { $ctrl = $c[0]; break }
}
$script:ctrlAddr = [uint64]0
if ($null -ne $ctrl) {
  try {
    $script:ctrlAddr = [uint64]$ctrl.MainModule.BaseAddress.ToInt64()
    $script:hCtrl = [H36]::OpenProcess(0x0410, $false, $ctrl.Id)
    Say ('[HDR] control process ' + $ctrl.ProcessName + ' pid=' + $ctrl.Id + ' base=0x' + ('{0:X}' -f $script:ctrlAddr) + ' handle=' + $script:hCtrl)
  } catch { Say ('[HDR] control process module query failed: ' + $_.Exception.Message) }
} else { Say '[HDR] no control process found' }

$selfProc = [System.Diagnostics.Process]::GetCurrentProcess()
$selfBase = [uint64]$selfProc.MainModule.BaseAddress.ToInt64()
Say ('[HDR] self pid=' + $selfProc.Id + ' base=0x' + ('{0:X}' -f $selfBase))

Say ''
Say '== [0b] harness processes / children =='
$pat = 'ward|guard|nprotect|npgg|xigncode|xhunter|battleye|easyanticheat|beservice|anticheat|cheat|sguard|tgame|tenprotect|ace-|vmprotect|themida|winlicense|obsidium|smartguard|hshield|stone|ezprotect|anti'
$hits = Get-Process | Where-Object { $_.ProcessName -match $pat } | Select-Object -First 20
if ($hits) { foreach ($h in $hits) { Say ('[PROC] ' + $h.Id + '  ' + $h.ProcessName + '  ' + $h.Path) } }
else { Say '[PROC] no anti-cheat-shaped process name matched' }
try {
  $kids = Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $script:gamePid) -ErrorAction Stop
  if ($kids) { foreach ($k in $kids) { Say ('[CHILD] ' + $k.ProcessId + '  ' + $k.Name + '  ' + $k.ExecutablePath) } }
  else { Say '[CHILD] WowB has no child processes' }
} catch { Say ('[CHILD] query failed: ' + $_.Exception.Message) }

# ---------------------------------------------------------------- [1] read honesty
Say ''
Say '== [1] read honesty matrix (K = kernel32 ReadProcessMemory, N = ntdll NtReadVirtualMemory) =='
function Probe([string]$tag, [IntPtr]$h, [uint64]$addr, [int]$n) {
  if ($h -eq [IntPtr]::Zero) { Say ('[RD-' + $tag + '] no handle'); return }
  $k = K32Read $h $addr $n
  $nn = NtRead $h $addr $n
  Say ('[RD-' + $tag + '] addr=0x' + ('{0:X}' -f $addr))
  Say ('[RD-' + $tag + ']   K ok=' + $k.ok + ' got=' + $k.got + ' err=' + $k.err + ' bytes=' + (HexOf $k.bytes $n) + ' allzero=' + (ZeroOf $k.bytes $n))
  Say ('[RD-' + $tag + ']   N ok=' + $nn.ok + ' got=' + $nn.got + ' status=0x' + ('{0:X8}' -f $nn.st) + ' bytes=' + (HexOf $nn.bytes $n) + ' allzero=' + (ZeroOf $nn.bytes $n))
}
Probe 'wowb+0' $script:hWowb $script:base 16
Probe 'wowb+471B5C0' $script:hWowb ($script:base + [uint64]0x471B5C0) 16
Probe 'wowb+7B76AB8' $script:hWowb ($script:base + [uint64]0x7B76AB8) 16
Probe 'wowb+coldpg' $script:hWowb ($script:base + [uint64]0x2C24040) 16
if ($script:ctrlAddr -ne 0) { Probe 'ctrl+0' $script:hCtrl $script:ctrlAddr 16 }
Probe 'self+0' $script:hSelf $selfBase 16

# ---------------------------------------------------------------- [2] MBI matrix
Say ''
Say '== [2] VirtualQueryEx matrix (state 0x1000=commit 0x2000=reserve 0x10000=free) =='
MbiLine 'wowb+0' $script:hWowb $script:base
MbiLine 'wowb+471B5C0' $script:hWowb ($script:base + [uint64]0x471B5C0)
MbiLine 'wowb+2C24040' $script:hWowb ($script:base + [uint64]0x2C24040)
MbiLine 'wowb+7B76AB8' $script:hWowb ($script:base + [uint64]0x7B76AB8)
if ($script:ctrlAddr -ne 0) { MbiLine 'ctrl+0' $script:hCtrl $script:ctrlAddr }
MbiLine 'self+0' $script:hSelf $selfBase

# ---------------------------------------------------------------- [3] write honesty
Say ''
Say '== [3] write honesty on a page WE allocate (target pattern 1122334455667788...) =='
$page = [H36]::VirtualAllocEx($script:hWowb, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
if ($page -eq [IntPtr]::Zero) { Say ('[WR] VirtualAllocEx failed err=' + (GetLastErr)) }
else {
  $pva = [uint64]$page.ToInt64()
  Say ('[WR] page = 0x' + ('{0:X}' -f $pva) + ' (RWX, private)')
  MbiLine 'ourpage' $script:hWowb $pva
  $pat2 = NewBytes 16
  for ($i = 0; $i -lt 16; $i++) { $pat2[$i] = [byte](0x10 + $i) }
  if ($NoWrite) {
    Say '[WR] -NoWrite: skipping the write test'
  } else {
    $w1 = K32Write $script:hWowb $pva $pat2
    Say ('[WR] K32 write ok=' + $w1.ok + ' wrote=' + $w1.got + ' err=' + $w1.err)
    $w2 = NtWrite $script:hWowb ($pva + [uint64]0x20) $pat2
    Say ('[WR] Nt  write ok=' + $w2.ok + ' wrote=' + $w2.got + ' status=0x' + ('{0:X8}' -f $w2.st))
    $r1 = K32Read $script:hWowb $pva 16
    Say ('[WR] K32 read  ok=' + $r1.ok + ' got=' + $r1.got + ' bytes=' + (HexOf $r1.bytes 16) + ' matches=' + ((HexOf $r1.bytes 16) -eq (HexOf $pat2 16)))
    $r2 = NtRead $script:hWowb $pva 16
    Say ('[WR] Nt  read  ok=' + $r2.ok + ' got=' + $r2.got + ' bytes=' + (HexOf $r2.bytes 16))
    $r3 = K32Read $script:hWowb ($pva + [uint64]0x20) 16
    Say ('[WR] K32 read+0x20 (Nt-written) ok=' + $r3.ok + ' bytes=' + (HexOf $r3.bytes 16))
    Start-Sleep -Milliseconds 1500
    $r4 = K32Read $script:hWowb $pva 16
    Say ('[WR] after 1.5 s: bytes=' + (HexOf $r4.bytes 16) + ' still_there=' + ((HexOf $r4.bytes 16) -eq (HexOf $pat2 16)))
    MbiLine 'ourpage2' $script:hWowb $pva
  }
  [void][H36]::VirtualFreeEx($script:hWowb, $page, [IntPtr]::Zero, 0x8000)
  Say '[WR] page freed'
}

# ---------------------------------------------------------------- [4] protection inventory
Say ''
Say '== [4] what WowB ships with / loads =='
try {
  $mods = $proc.Modules
  Say ('[MOD] WowB has ' + $mods.Count + ' modules; listing every one outside C:\Windows')
  foreach ($m in $mods) {
    $fn = ''
    try { $fn = $m.FileName } catch { }
    if ($fn -eq '' -or $fn -notlike 'C:\Windows\*') {
      $sus = ''
      if ($m.ModuleName -match $pat) { $sus = '   <== SUSPICIOUS NAME' }
      Say ('[MOD] ' + $m.ModuleName + '   ' + $fn + $sus)
    }
  }
} catch { Say ('[MOD] enumeration failed: ' + $_.Exception.Message) }

Say ''
Say '== [4b] running kernel drivers outside C:\Windows =='
try {
  $drv = Get-CimInstance Win32_SystemDriver -ErrorAction Stop | Where-Object { $_.State -eq 'Running' }
  $n = 0
  foreach ($d in $drv) {
    $p = ''
    if ($d.PathName) { $p = $d.PathName }
    $sus = ''
    if ($d.Name -match $pat -or $p -match $pat) { $sus = '   <== SUSPICIOUS NAME' }
    if ($p -notlike '*\System32\drivers\*' -or $sus -ne '') {
      $n++
      if ($n -le 60) { Say ('[DRV] ' + $d.Name + '   ' + $p + $sus) }
    }
  }
  Say ('[DRV] listed ' + $n + ' non-system or suspicious running drivers')
} catch { Say ('[DRV] driver query failed: ' + $_.Exception.Message) }

# ---------------------------------------------------------------- [5] verdicts
Say ''
Say '== [5] verdicts =='
$k = K32Read $script:hWowb $script:base 4
$n = NtRead $script:hWowb $script:base 4
if ($k.ok -and -not (ZeroOf $k.bytes 4)) {
  Say '[V] WowB base+0 is readable and non-zero -> reads of this process are NOT wholesale faked'
} else {
  Say '[V] WowB base+0 came back empty/zero -> foreign reads of this process are being neutralised (or the image is not where we think)'
}
if ($script:ctrlAddr -ne 0) {
  $c = K32Read $script:hCtrl $script:ctrlAddr 4
  if ((HexOf $c.bytes 4) -eq '4d5a9000') { Say '[V] control process reads fine (4D 5A 90 00) -> the anomaly is specific to WowB' }
  else { Say ('[V] control process base+0 = ' + (HexOf $c.bytes 4) + ' -> reads are broken in OUR process, not just for WowB') }
}
Say ('[V] self base+0 = ' + (HexOf (K32Read $script:hSelf $selfBase 4).bytes 4) + ' (must also be 4d5a9000)')
if ($script:hCtrl -ne [IntPtr]::Zero) { [void][H36]::CloseHandle($script:hCtrl) }
[void][H36]::CloseHandle($script:hWowb)
Say ''
Say '[DONE] upload C:\ghidra-scripts\hunt36-out.txt'
