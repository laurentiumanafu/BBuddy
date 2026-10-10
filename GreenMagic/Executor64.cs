using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GreenMagic.Native;

namespace GreenMagic
{
    /// <summary>
    /// Code-execution engine for NATIVE 64-BIT target processes (e.g.
    /// WowB.exe build 70291). The 32-bit <see cref="Executor"/> /
    /// <see cref="ExecutorRand"/> cannot be used there: they hook
    /// d3d9!EndScene (a 64-bit client has no d3d9.dll), emit pushad/popad
    /// (undefined in x64 long mode) and use the i386 CONTEXT.
    ///
    /// Mechanism — RIP hijacking:
    ///   1. Suspend the game's main thread, capture the full AMD64 context.
    ///   2. Point RIP at a code cave inside the target, resume.
    ///   3. The cave executes the payload (x64 fastcall calls into game
    ///      functions), stores the result, signals a shared kernel event,
    ///      then spins in place.
    ///   4. On the event we suspend the thread again, restore the saved
    ///      context (game resumes exactly where we found it) and resume.
    ///
    /// One full hijack round-trip per Execute() call. A per-frame hook
    /// (DX11 Present vtable swap, see <see cref="DX11PresentHook64"/>)
    /// removes the round-trip cost once graphics offsets are available.
    ///
    /// kernel32 entry points are resolved from OUR kernel32 module
    /// (same OS session => same base in the target; the same assumption
    /// the 32-bit Executor makes).
    /// </summary>
    public sealed class Executor64 : IDisposable
    {
        // Layout of the shared data page in the target.
        private const int OffWaitHandle = 0x00;      // u32
        private const int OffContinueHandle = 0x04;  // u32 (reserved)
        private const int OffFinishedHandle = 0x08;  // u32
        private const int OffReturnValue = 0x10;     // u64

        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageReadWrite = 0x04;
        private const uint MemCommit = 0x1000;

        private readonly Memory _memory;

        private ulong _cave;        // RWX code cave
        private ulong _dataPage;    // RW data (handles + return value)
        private ulong _namePage;    // RW event names (ANSI)

        private readonly string _finishedEventName;
        private EventWaitHandle? _finishedEvent;

        private ulong _createEventA;
        private ulong _setEvent;

        /// <summary>
        /// Pristine copy of the thread context captured before the hijack.
        /// MUST stay unmutated — it is what RestoreContext writes back.
        /// </summary>
        private Context64 _originalContext;
        private bool _contextSaved;

        public bool IsInitialized { get; private set; }
        public ulong CavePointer => _cave;
        public ulong DataPointer => _dataPage;

        /// <summary>
        /// The local end of the shared "finished" kernel event. Used by
        /// <see cref="DX11PresentHook64.ExecuteOnFrame"/> to wait for
        /// payloads that run inside the Present gate.
        /// </summary>
        public EventWaitHandle FinishedEvent => _finishedEvent!;

        /// <summary>The target-side handle of the "finished" event.</summary>
        public uint FinishedHandle => _memory.Read64<uint>(_dataPage + OffFinishedHandle);

        public Executor64(Memory memory)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));

            if (!memory.Is64BitTarget)
                throw new InvalidOperationException("Executor64 requires a native 64-bit target process.");

            _finishedEventName = "Global\\" + RandomString(24);

            Initialize();
        }

        private static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            byte[] seed = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetNonZeroBytes(seed);
            var random = new Random(BitConverter.ToInt32(seed, 0));

            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                sb.Append(chars[random.Next(chars.Length)]);
            return sb.ToString();
        }

        // --------------------------------------------------------------
        // Initialization
        // --------------------------------------------------------------

        private void Initialize()
        {
            _cave = _memory.AllocateMemory64(4096, MemCommit, PageExecuteReadWrite);
            _dataPage = _memory.AllocateMemory64(4096, MemCommit, PageReadWrite);
            _namePage = _memory.AllocateMemory64(4096, MemCommit, PageReadWrite);
            if (_cave == 0 || _dataPage == 0 || _namePage == 0)
                throw new Exception("Executor64: VirtualAllocEx in the target failed.");

            // The target's CreateEventA will OPEN this already-existing
            // named object, so target and bot share one kernel event.
            _finishedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _finishedEventName);

            _createEventA = ResolveKernel32("CreateEventA");
            _setEvent = ResolveKernel32("SetEvent");

            // Write the event name into the target (ANSI, null-terminated).
            byte[] nameBytes = Encoding.ASCII.GetBytes(_finishedEventName);
            Array.Resize(ref nameBytes, nameBytes.Length + 1);
            _memory.WriteBytes64(_namePage, nameBytes);

            RunInitStub();
            IsInitialized = true;
        }

        private static ulong ResolveKernel32(string procName)
        {
            IntPtr k32 = Imports.GetModuleHandleW("kernel32.dll");
            if (k32 == IntPtr.Zero)
                throw new Exception("Executor64: could not get local kernel32.dll handle.");

            UIntPtr proc = Imports.GetProcAddress(k32, procName);
            if (proc == UIntPtr.Zero)
                throw new Exception($"Executor64: could not resolve {procName}.");

            return (ulong)proc.ToUInt64();
        }

        /// <summary>
        /// One-shot stub: creates the shared "finished" event inside the
        /// target, stores its handle in the data page, signals it, spins.
        /// </summary>
        private void RunInitStub()
        {
            var em = new X64Emitter();

            // CreateEventA(NULL, bManualReset=FALSE, bInitialState=FALSE, name)
            em.XorRegReg32(X64Emitter.Rcx);
            em.XorRegReg32(X64Emitter.Rdx);
            em.XorRegReg32(X64Emitter.R8);
            em.MovRegImm64(X64Emitter.R9, _namePage);
            em.MovRegImm64(X64Emitter.Rax, _createEventA);
            em.CallReg(X64Emitter.Rax);

            em.TestRaxRax();
            em.JzLabel("@spin");

            // store handle (low 32 bits) at dataPage+OffFinishedHandle
            em.StoreEaxToAbs(_dataPage + OffFinishedHandle);

            // SetEvent(handle)
            em.LoadEcxFromAbs(_dataPage + OffFinishedHandle);
            em.MovRegImm64(X64Emitter.Rax, _setEvent);
            em.CallReg(X64Emitter.Rax);

            em.Label("@spin");
            em.SpinLoop();

            RunCave(em.ToArray(), TimeSpan.FromSeconds(10), expectResult: false);
        }

        // --------------------------------------------------------------
        // Execution
        // --------------------------------------------------------------

        /// <summary>
        /// Calls a function in the target using the x64 fastcall ABI and
        /// returns RAX. Up to 4 args travel in rcx/rdx/r8/r9, the rest on
        /// the stack; 32 bytes of shadow space and 16-byte RSP alignment
        /// are emitted automatically.
        /// </summary>
        public ulong Execute(ulong functionAddress, params ulong[] args)
        {
            CheckReady();

            var em = new X64Emitter();
            em.EmitFastCall(functionAddress, args);

            // store RAX into the data page
            em.StoreRegToAbs(X64Emitter.Rax, _dataPage + OffReturnValue);

            // SetEvent(finishedHandle)
            em.LoadEcxFromAbs(_dataPage + OffFinishedHandle);
            em.MovRegImm64(X64Emitter.Rax, _setEvent);
            em.CallReg(X64Emitter.Rax);

            em.SpinLoop();

            RunCave(em.ToArray(), TimeSpan.FromSeconds(10), expectResult: true);
            return _memory.Read64<ulong>(_dataPage + OffReturnValue);
        }

        /// <summary>
        /// Runs raw machine code in the target. The code MUST end with
        /// SetEvent on the finished handle (or accept the 10 s timeout);
        /// it should preserve nothing — the full thread context is saved
        /// and restored around the run.
        /// </summary>
        public void ExecuteRaw(byte[] code)
        {
            CheckReady();
            if (code.Length > 3072)
                throw new ArgumentException("Payload exceeds the code cave size.");
            RunCave(code, TimeSpan.FromSeconds(10), expectResult: false);
        }

        private void CheckReady()
        {
            if (!IsInitialized || !_memory.IsProcessOpen || !_memory.IsThreadOpen)
                throw new Exception("Executor64: not initialized or target process/thread not open.");
        }

        /// <summary>
        /// The hijack round-trip: write code to the cave, redirect RIP to
        /// it, wait for the finished event, then restore the context.
        /// </summary>
        private void RunCave(byte[] code, TimeSpan timeout, bool expectResult)
        {
            _finishedEvent!.Reset();

            // clear previous result + write the payload
            if (expectResult)
                _memory.WriteBytes64(_dataPage + OffReturnValue, new byte[8]);
            _memory.WriteBytes64(_cave, code);

            HijackTo(_cave);
            try
            {
                if (!_finishedEvent.WaitOne(timeout))
                    throw new Exception("Executor64: payload never signalled the finished event (target hung or code faulted).");
            }
            finally
            {
                RestoreContext();
            }
        }

        private void HijackTo(ulong rip)
        {
            _memory.SuspendThread();
            try
            {
                _originalContext = _memory.GetThreadContext64(ContextFlags64.CONTEXT_ALL);
                if (_originalContext.ContextFlags == 0)
                    throw new Exception("Executor64: GetThreadContext failed on the target thread.");

                _contextSaved = true;

                // mutate a COPY — _originalContext must keep the real Rip
                Context64 hijacked = _originalContext;
                hijacked.Rip = rip;
                hijacked.ContextFlags = (uint)ContextFlags64.CONTEXT_ALL;
                if (!_memory.SetThreadContext64(hijacked))
                    throw new Exception("Executor64: SetThreadContext failed.");
            }
            finally
            {
                _memory.ResumeThread();
            }
        }

        private void RestoreContext()
        {
            if (!_contextSaved)
                return;

            _memory.SuspendThread();
            try
            {
                // full restore: original Rip, registers, flags, debug regs
                _memory.SetThreadContext64(_originalContext);
                _contextSaved = false;
            }
            finally
            {
                _memory.ResumeThread();
            }
        }

        // --------------------------------------------------------------
        // Cleanup
        // --------------------------------------------------------------

        public void Dispose()
        {
            if (_memory.IsProcessOpen)
            {
                if (_cave != 0) _memory.FreeMemory64(_cave);
                if (_dataPage != 0) _memory.FreeMemory64(_dataPage);
                if (_namePage != 0) _memory.FreeMemory64(_namePage);
            }

            _finishedEvent?.Close();
            IsInitialized = false;
        }
    }
}
