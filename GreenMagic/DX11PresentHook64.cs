using System;
using System.Threading;
using GreenMagic.Native;

namespace GreenMagic
{
    /// <summary>
    /// IDXGISwapChain::Present hook for native 64-bit targets — the x64
    /// replacement for the d3d9!EndScene detour used by the 32-bit
    /// executors (a modern client renders with DX11/DX12; there is no
    /// d3d9.dll to hook).
    ///
    /// Technique — VTABLE SLOT SWAP (no inline patching, no disassembler):
    /// the vtable entry for Present is just an 8-byte pointer, so we
    /// VirtualProtectEx the vtable slot, replace the pointer with our
    /// cave, and remember the original. The cave runs the injection gate
    /// once per frame and tail-jumps to the original Present with the
    /// caller's arguments untouched — zero instruction copying.
    ///
    ///     IDXGISwapChain vtable:  0 QueryInterface ... 7 GetDevice,
    ///                             8 Present, 9 GetBuffer ...
    ///
    /// The gate checks a control page each frame; when a payload is
    /// queued (SetPayload) it executes it inline on the render thread —
    /// the same thread-affinity guarantee the EndScene detour provided.
    /// </summary>
    public sealed class DX11PresentHook64 : IDisposable
    {
        /// <summary>IDXGISwapChain::Present vtable index.</summary>
        public const int PresentVtableIndex = 8;

        private const uint PageReadWrite = 0x04;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint MemCommit = 0x1000;

        // Control page layout (target memory)
        private const int CtrlFunc = 0x00;        // u64 — queued function, 0 = none
        private const int CtrlArgs = 0x08;        // 4 x u64 fastcall args
        private const int CtrlDoneHandle = 0x28;  // u32 — event to signal after run
        private const int CtrlResult = 0x30;      // u64 — return value
        private const int CtrlOrigPresent = 0x38; // u64 — original Present address

        private readonly Memory _memory;
        private ulong _swapChain;
        private ulong _vtable;
        private ulong _origPresent;
        private ulong _cave;
        private ulong _controlPage;

        public bool IsInstalled { get; private set; }
        public ulong OriginalPresent => _origPresent;
        public ulong ControlPage => _controlPage;

        public DX11PresentHook64(Memory memory, ulong swapChainAddress)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));

            if (!memory.Is64BitTarget)
                throw new InvalidOperationException("DX11PresentHook64 requires a native 64-bit target.");
            if (swapChainAddress == 0)
                throw new ArgumentException("Swap chain address is null — fill WowBOffsets.SwapChainPtr first.");

            _swapChain = swapChainAddress;
        }

        /// <summary>
        /// Installs the hook. Kernel32 addresses are resolved from the
        /// local kernel32 (same assumption as Executor/Executor64).
        /// </summary>
        public void Install()
        {
            if (IsInstalled)
                return;

            _vtable = _memory.ReadPointer64(_swapChain);
            if (_vtable == 0)
                throw new Exception("DX11PresentHook64: swap chain has no vtable pointer.");

            _origPresent = _memory.ReadPointer64(_vtable + (ulong)PresentVtableIndex * 8);
            if (_origPresent == 0)
                throw new Exception("DX11PresentHook64: could not read the original Present pointer.");

            _cave = _memory.AllocateMemory64(4096, MemCommit, PageExecuteReadWrite);
            _controlPage = _memory.AllocateMemory64(4096, MemCommit, PageReadWrite);
            if (_cave == 0 || _controlPage == 0)
                throw new Exception("DX11PresentHook64: allocation in the target failed.");

            _memory.WriteBytes64(_controlPage + CtrlOrigPresent, BitConverter.GetBytes(_origPresent));

            ulong setEvent = ResolveKernel32("SetEvent");
            _memory.WriteBytes64(_cave, BuildCave(setEvent));

            // Swap the vtable slot (protect -> write -> restore protection)
            ulong slotAddress = _vtable + (ulong)PresentVtableIndex * 8;
            if (!Imports.VirtualProtectEx64(_memory.ProcessHandle, new IntPtr((long)slotAddress), (IntPtr)8, PageReadWrite, out uint oldProtect))
                throw new System.ComponentModel.Win32Exception();

            if (!_memory.WritePointer64(slotAddress, _cave))
            {
                Imports.VirtualProtectEx64(_memory.ProcessHandle, new IntPtr((long)slotAddress), (IntPtr)8, oldProtect, out _);
                throw new Exception("DX11PresentHook64: failed to write the vtable slot.");
            }

            Imports.VirtualProtectEx64(_memory.ProcessHandle, new IntPtr((long)slotAddress), (IntPtr)8, oldProtect, out _);
            IsInstalled = true;
        }

        /// <summary>
        /// Queues a fastcall call to run on the render thread during the
        /// NEXT Present and blocks until it ran (or timeout). Returns the
        /// call's RAX. The <paramref name="executor"/> supplies the shared
        /// "finished" kernel event (its target-side handle is written into
        /// the control page so the gate can signal it).
        /// </summary>
        public ulong ExecuteOnFrame(Executor64 executor, ulong functionAddress, params ulong[] args)
        {
            if (!IsInstalled)
                throw new InvalidOperationException("Hook is not installed.");
            if (args.Length > 4)
                throw new ArgumentException("Only the 4 register arguments are supported by the Present gate.");
            if (executor == null)
                throw new ArgumentNullException(nameof(executor));

            // Clear any previous result, then publish the queued call.
            // The function pointer is written LAST: the gate treats a
            // non-zero function slot as "payload ready".
            _memory.WriteBytes64(_controlPage + CtrlResult, new byte[8]);

            var ctrl = new byte[CtrlArgs - CtrlFunc]; // args block only
            for (int i = 0; i < args.Length; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(args[i]), 0, ctrl, i * 8, 8);
            _memory.WriteBytes64(_controlPage + CtrlArgs, ctrl);

            _memory.WriteBytes64(_controlPage + CtrlDoneHandle, BitConverter.GetBytes(executor.FinishedHandle));

            executor.FinishedEvent.Reset();
            _memory.WriteBytes64(_controlPage + CtrlFunc, BitConverter.GetBytes(functionAddress));

            if (!executor.FinishedEvent.WaitOne(TimeSpan.FromSeconds(10)))
                throw new Exception("DX11PresentHook64: queued payload was not consumed within 10 s (game not rendering?).");

            return _memory.Read64<ulong>(_controlPage + CtrlResult);
        }

        /// <summary>Restores the original Present pointer.</summary>
        public void Uninstall()
        {
            if (!IsInstalled)
                return;

            ulong slotAddress = _vtable + (ulong)PresentVtableIndex * 8;
            if (Imports.VirtualProtectEx64(_memory.ProcessHandle, new IntPtr((long)slotAddress), (IntPtr)8, PageReadWrite, out uint oldProtect))
            {
                _memory.WritePointer64(slotAddress, _origPresent);
                Imports.VirtualProtectEx64(_memory.ProcessHandle, new IntPtr((long)slotAddress), (IntPtr)8, oldProtect, out _);
            }
            IsInstalled = false;
        }

        // --------------------------------------------------------------
        // Cave builder
        // --------------------------------------------------------------

        /// <summary>
        /// Builds the Present replacement code:
        ///   entry: rcx=swapchain, edx=SyncInterval, r8d=Flags
        ///   1. save args + volatiles + flags, align stack
        ///   2. gate: if controlPage+CtrlFunc != 0 -> fastcall it with the
        ///      queued args, store RAX, clear the slot, SetEvent(done)
        ///   3. restore stack/registers, tail-jmp original Present
        /// </summary>
        private byte[] BuildCave(ulong setEvent)
        {
            ulong ctrlFunc = _controlPage + CtrlFunc;
            ulong ctrlDone = _controlPage + CtrlDoneHandle;
            ulong ctrlResult = _controlPage + CtrlResult;
            ulong ctrlOrig = _controlPage + CtrlOrigPresent;

            var em = new X64Emitter();

            // ---- preserve the incoming call state ----
            em.PushReg(X64Emitter.Rcx);
            em.PushReg(X64Emitter.Rdx);
            em.PushReg(X64Emitter.R8);
            em.PushReg(X64Emitter.R9);
            em.PushReg(X64Emitter.Rax);
            em.PushReg(X64Emitter.R10);
            em.PushFq();
            em.MovRegReg(X64Emitter.Rbp, X64Emitter.Rsp);
            em.AlignRsp16();
            em.SubRspImm32(0x20); // shadow space for our own calls

            // ---- gate ----
            em.LoadRegFromAbs(X64Emitter.Rax, ctrlFunc);
            em.TestRaxRax();
            em.JzLabel("@tail");

            // args 0..3 from the control page into rcx/rdx/r8/r9
            em.LoadRegFromAbs(X64Emitter.Rcx, _controlPage + CtrlArgs + 0);
            em.LoadRegFromAbs(X64Emitter.Rdx, _controlPage + CtrlArgs + 8);
            em.LoadRegFromAbs(X64Emitter.R8, _controlPage + CtrlArgs + 16);
            em.LoadRegFromAbs(X64Emitter.R9, _controlPage + CtrlArgs + 24);
            em.CallReg(X64Emitter.Rax);

            // store result, clear the slot
            em.StoreRegToAbs(X64Emitter.Rax, ctrlResult);
            em.XorRegReg32(X64Emitter.Rax);
            em.StoreRegToAbs(X64Emitter.Rax, ctrlFunc);

            // SetEvent(doneHandle)
            em.LoadEcxFromAbs(ctrlDone);
            em.MovRegImm64(X64Emitter.Rax, setEvent);
            em.CallReg(X64Emitter.Rax);

            // ---- tail call into the real Present ----
            em.Label("@tail");
            em.AddRspImm32(0x20);
            em.MovRegReg(X64Emitter.Rsp, X64Emitter.Rbp);
            em.PopFq();
            em.PopReg(X64Emitter.R10);
            em.PopReg(X64Emitter.Rax);
            em.PopReg(X64Emitter.R9);
            em.PopReg(X64Emitter.R8);
            em.PopReg(X64Emitter.Rdx);
            em.PopReg(X64Emitter.Rcx);

            // mov rax, originalPresent ; jmp rax  (args still in place)
            em.LoadRegFromAbs(X64Emitter.Rax, ctrlOrig);
            em.JmpReg(X64Emitter.Rax);

            return em.ToArray();
        }

        private static ulong ResolveKernel32(string procName)
        {
            IntPtr k32 = Imports.GetModuleHandleW("kernel32.dll");
            if (k32 == IntPtr.Zero)
                throw new Exception("DX11PresentHook64: could not get local kernel32.dll handle.");
            UIntPtr proc = Imports.GetProcAddress(k32, procName);
            if (proc == UIntPtr.Zero)
                throw new Exception($"DX11PresentHook64: could not resolve {procName}.");
            return (ulong)proc.ToUInt64();
        }

        public void Dispose()
        {
            try { Uninstall(); } catch { /* target may already be gone */ }

            if (_memory.IsProcessOpen)
            {
                if (_cave != 0) _memory.FreeMemory64(_cave);
                if (_controlPage != 0) _memory.FreeMemory64(_controlPage);
            }
        }
    }
}
