using System;
using System.Collections.Generic;
using System.Text;

namespace GreenMagic
{
    /// <summary>
    /// Minimal x86-64 machine-code emitter.
    ///
    /// fasmdll_managed.dll (used by <see cref="Memory.Asm"/>) is a 32-bit
    /// native binary and cannot be loaded by a 64-bit CopilotBuddy, so the
    /// x64 executor emits machine code directly. Only the instructions the
    /// executor needs are implemented:
    ///
    ///   * mov reg, imm64 / push / pop / ret / pushfq / popfq
    ///   * and rsp, -16 ; sub/add rsp, imm32           (fastcall framing)
    ///   * call reg / jmp reg / jmp rel32 / jcc rel32   (control flow)
    ///   * xor / test                                    (zeroing, checks)
    ///   * loads/stores through an r11 indirection       (absolute 64-bit
    ///     addresses cannot be encoded in ModR/M memory operands)
    ///
    /// r11 is reserved as scratch register (<see cref="ScratchReg"/>).
    /// </summary>
    public sealed class X64Emitter
    {
        // Register indices
        public const int Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rbp = 5, Rsi = 6, Rdi = 7;
        public const int R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;

        /// <summary>Register used internally for absolute-address indirection.</summary>
        public const int ScratchReg = R11;

        private readonly List<byte> _code = new List<byte>(512);
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<JumpFixup> _fixups = new List<JumpFixup>();

        private struct JumpFixup
        {
            public int PatchOffset;   // position of the rel32 field
            public string Label;
        }

        public int Position => _code.Count;

        // --------------------------------------------------------------
        // Raw emission
        // --------------------------------------------------------------

        public void EmitByte(byte b) => _code.Add(b);

        public void EmitBytes(params byte[] bytes) => _code.AddRange(bytes);

        private void EmitU32(uint v)
        {
            _code.Add((byte)v);
            _code.Add((byte)(v >> 8));
            _code.Add((byte)(v >> 16));
            _code.Add((byte)(v >> 24));
        }

        private void EmitU64(ulong v)
        {
            EmitU32((uint)v);
            EmitU32((uint)(v >> 32));
        }

        // --------------------------------------------------------------
        // Instructions
        // --------------------------------------------------------------

        /// <summary>mov reg, imm64 (REX.W B8+rd io).</summary>
        public void MovRegImm64(int reg, ulong value)
        {
            if (reg >= 8)
            {
                EmitByte(0x49);
                EmitByte((byte)(0xB8 + (reg - 8)));
            }
            else
            {
                EmitByte(0x48);
                EmitByte((byte)(0xB8 + reg));
            }
            EmitU64(value);
        }

        /// <summary>push reg (50+rd / REX.B 50+rd).</summary>
        public void PushReg(int reg)
        {
            if (reg >= 8)
            {
                EmitByte(0x41);
                EmitByte((byte)(0x50 + (reg - 8)));
            }
            else
            {
                EmitByte((byte)(0x50 + reg));
            }
        }

        /// <summary>pop reg (58+rd / REX.B 58+rd).</summary>
        public void PopReg(int reg)
        {
            if (reg >= 8)
            {
                EmitByte(0x41);
                EmitByte((byte)(0x58 + (reg - 8)));
            }
            else
            {
                EmitByte((byte)(0x58 + reg));
            }
        }

        /// <summary>mov dst, src (REX.W 89 /r) for full 64-bit registers.</summary>
        public void MovRegReg(int dst, int src)
        {
            if (dst >= 8 || src >= 8)
                EmitByte((byte)(0x48 | (src >= 8 ? 0x04 : 0) | (dst >= 8 ? 0x01 : 0)));
            else
                EmitByte(0x48);
            EmitByte((byte)0x89);
            EmitByte((byte)(0xC0 | ((src & 7) << 3) | (dst & 7)));
        }

        /// <summary>and rsp, -16 (48 83 E4 F0) — stack alignment for fastcall.</summary>
        public void AlignRsp16() => EmitBytes(0x48, 0x83, 0xE4, 0xF0);

        /// <summary>sub rsp, imm32 (48 81 EC id).</summary>
        public void SubRspImm32(int imm32)
        {
            EmitBytes(0x48, 0x81, 0xEC);
            EmitU32(unchecked((uint)imm32));
        }

        /// <summary>add rsp, imm32 (48 81 C4 id).</summary>
        public void AddRspImm32(int imm32)
        {
            EmitBytes(0x48, 0x81, 0xC4);
            EmitU32(unchecked((uint)imm32));
        }

        /// <summary>xor reg32, reg32 — canonical zeroing (31 /r, REX.RB for r8d-r15d).</summary>
        public void XorRegReg32(int reg)
        {
            if (reg >= 8)
            {
                EmitByte(0x45);
                EmitByte((byte)(0xC0 | ((reg - 8) << 3) | (reg - 8)));
            }
            else
            {
                EmitByte(0x31);
                EmitByte((byte)(0xC0 | (reg << 3) | reg));
            }
        }

        /// <summary>test rax, rax (48 85 C0).</summary>
        public void TestRaxRax() => EmitBytes(0x48, 0x85, 0xC0);

        /// <summary>call reg (FF /2).</summary>
        public void CallReg(int reg)
        {
            if (reg >= 8)
                EmitBytes(0x41, (byte)(0xFF, 0xD0 + (reg - 8)));
            else
                EmitBytes(0xFF, (byte)(0xD0 + reg));
        }

        /// <summary>jmp reg (FF /4).</summary>
        public void JmpReg(int reg)
        {
            if (reg >= 8)
                EmitBytes(0x41, (byte)(0xFF, 0xE0 + (reg - 8)));
            else
                EmitBytes(0xFF, (byte)(0xE0 + reg));
        }

        /// <summary>ret (C3).</summary>
        public void Ret() => EmitByte(0xC3);

        /// <summary>pushfq (9C).</summary>
        public void PushFq() => EmitByte(0x9C);

        /// <summary>popfq (9D).</summary>
        public void PopFq() => EmitByte(0x9D);

        // ---- memory access through the r11 scratch register ----

        /// <summary>
        /// mov [absAddr], reg64 — via: mov r11, imm64 ; mov [r11], reg.
        /// </summary>
        public void StoreRegToAbs(int reg, ulong absAddr)
        {
            MovRegImm64(ScratchReg, absAddr);
            if (reg >= 8)
                EmitBytes(0x49, 0x89, (byte)(0x03 | ((reg - 8) << 3)));
            else
                EmitBytes(0x49, 0x89, (byte)(0x03 | (reg << 3)));
        }

        /// <summary>
        /// mov reg64, [absAddr] — via: mov r11, imm64 ; mov reg, [r11].
        /// </summary>
        public void LoadRegFromAbs(int reg, ulong absAddr)
        {
            MovRegImm64(ScratchReg, absAddr);
            if (reg >= 8)
                EmitBytes(0x49, 0x8B, (byte)(0x03 | ((reg - 8) << 3)));
            else
                EmitBytes(0x49, 0x8B, (byte)(0x03 | (reg << 3)));
        }

        /// <summary>mov [absAddr], eax — store the low 32 bits of rax.</summary>
        public void StoreEaxToAbs(ulong absAddr)
        {
            MovRegImm64(ScratchReg, absAddr);
            EmitBytes(0x41, 0x89, 0x03);
        }

        /// <summary>mov ecx, [absAddr] — load a 32-bit value (e.g. an event handle).</summary>
        public void LoadEcxFromAbs(ulong absAddr)
        {
            MovRegImm64(ScratchReg, absAddr);
            EmitBytes(0x41, 0x8B, 0x0B);
        }

        // ---- labels / jumps ----

        /// <summary>Binds a label at the current position.</summary>
        public void Label(string name) => _labels[name] = _code.Count;

        /// <summary>jmp rel32 to a label (E9 cd).</summary>
        public void JmpLabel(string label)
        {
            EmitByte(0xE9);
            _fixups.Add(new JumpFixup { PatchOffset = _code.Count, Label = label });
            EmitU32(0);
        }

        /// <summary>jz rel32 to a label (0F 84 cd).</summary>
        public void JzLabel(string label) => JccLabel(0x84, label);

        /// <summary>jnz rel32 to a label (0F 85 cd).</summary>
        public void JnzLabel(string label) => JccLabel(0x85, label);

        /// <summary>Any two-byte conditional jump (0F 8x cd) to a label.</summary>
        public void JccLabel(byte opcode2, string label)
        {
            EmitByte(0x0F);
            EmitByte(opcode2);
            _fixups.Add(new JumpFixup { PatchOffset = _code.Count, Label = label });
            EmitU32(0);
        }

        /// <summary>jmp to the current position — infinite loop ("jmp $").</summary>
        public void SpinLoop()
        {
            // EB FE : jmp rel8 -2
            EmitBytes(0xEB, 0xFE);
        }

        // --------------------------------------------------------------
        // x64 fastcall framing
        // --------------------------------------------------------------

        /// <summary>
        /// Begins an x64 fastcall frame: saves rbp, aligns RSP to 16 bytes
        /// and reserves 32 bytes of shadow space plus room for stack args.
        /// Must be paired with <see cref="EndCallFrame"/>.
        /// </summary>
        public int BeginCallFrame(int argCount)
        {
            PushReg(Rbp);
            MovRegReg(Rbp, Rsp);
            AlignRsp16();

            int stackArgSlots = Math.Max(0, argCount - 4);
            int frameSize = 0x20 + stackArgSlots * 8;
            frameSize = (frameSize + 15) & ~15; // keep 16-byte alignment for the CALL
            SubRspImm32(frameSize);
            return frameSize;
        }

        /// <summary>Tears down a frame opened by <see cref="BeginCallFrame"/>.</summary>
        public void EndCallFrame(int frameSize)
        {
            AddRspImm32(frameSize);
            MovRegReg(Rsp, Rbp);
            PopReg(Rbp);
        }

        /// <summary>
        /// Emits a complete x64 fastcall call: frame, argument setup
        /// (rcx/rdx/r8/r9 then stack), call through rax, no result handling.
        /// NOTE: integer/pointer args only — floating-point args (which the
        /// ABI passes in xmm0-xmm3) are not supported yet.
        /// </summary>
        public void EmitFastCall(ulong target, params ulong[] args)
        {
            int frame = BeginCallFrame(args.Length);

            // stack arguments (arg index 4+)
            for (int i = 4; i < args.Length; i++)
            {
                // mov rax, imm64 ; mov [rsp + 0x20 + (i-4)*8], rax
                MovRegImm64(Rax, args[i]);
                int off = 0x20 + (i - 4) * 8;
                EmitBytes(0x48, 0x89, 0x84, 0x24);
                EmitU32(unchecked((uint)off));
            }

            if (args.Length > 0) MovRegImm64(Rcx, args[0]);
            if (args.Length > 1) MovRegImm64(Rdx, args[1]);
            if (args.Length > 2) MovRegImm64(R8, args[2]);
            if (args.Length > 3) MovRegImm64(R9, args[3]);

            MovRegImm64(Rax, target);
            CallReg(Rax);

            EndCallFrame(frame);
        }

        // --------------------------------------------------------------
        // Assembly
        // --------------------------------------------------------------

        /// <summary>
        /// Resolves label fixups and returns the machine code.
        /// </summary>
        public byte[] ToArray()
        {
            byte[] code = _code.ToArray();
            foreach (var fixup in _fixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out int target))
                    throw new InvalidOperationException($"Unresolved label '{fixup.Label}'");

                int rel = target - (fixup.PatchOffset + 4);
                code[fixup.PatchOffset] = (byte)rel;
                code[fixup.PatchOffset + 1] = (byte)(rel >> 8);
                code[fixup.PatchOffset + 2] = (byte)(rel >> 16);
                code[fixup.PatchOffset + 3] = (byte)(rel >> 24);
            }
            return code;
        }

        /// <summary>Hex dump of the current code (for diagnostics).</summary>
        public string ToHexDump()
        {
            var sb = new StringBuilder(_code.Count * 2);
            foreach (byte b in _code)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
