// Read-only, bounded differential research tool. No game actions or injection.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WowBCtmCapture
{
    internal static class Program
    {
        private const uint ProcessVmRead = 0x0010, ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000, MemImage = 0x1000000, MemPrivate = 0x20000;
        private const uint PageGuard = 0x100, PageNoAccess = 0x01;
        private const int ChunkSize = 1024 * 1024, MaxOutput = 200;
        private const float StableTolerance = 0.05f, MinChange = 3f, CoordinateLimit = 40000f;
        private const ulong MaxUserAddress = 0x00007fffffffffffUL;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(IntPtr handle, IntPtr address,
            out MemoryBasicInformation information, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address,
            [Out] byte[] buffer, UIntPtr count, out UIntPtr read);

        private sealed class Chunk
        {
            public ulong Address, AllocationBase;
            public int Length;
            public uint Type;
            public byte[]? I1, I2, A1, A2, B1, B2;
            public bool Dropped;
        }
        private sealed class PlayerAnchor
        {
            public ulong GuidLow, GuidHigh, Record, Unit, Slot, Pool, Elements;
            public uint Tag, Marker;
            public int Family, Index;
        }
        private sealed class Candidate
        {
            public Chunk Chunk = null!;
            public int Offset;
            public Vec Idle, A, B;
        }
        private readonly struct Vec
        {
            public readonly float X, Y, Z;
            public Vec(float x, float y, float z) { X = x; Y = y; Z = z; }
            public override string ToString() => string.Format(CultureInfo.InvariantCulture,
                "({0:F3}, {1:F3}, {2:F3})", X, Y, Z);
        }
        private sealed class Stats
        {
            public long PlannedBytes, ImageBytes, PrivateBytes, AnchorBytes, I1Bytes, I2Bytes, A1Bytes, A2Bytes, B1Bytes, B2Bytes;
            public long StableA, ChangedAB, StableB;
            public int I1Failed, I2Failed, A1Failed, A2Failed, B1Failed, B2Failed, RetainedChunks;
            public bool ScopeCapped;
            public ulong QueryEndedAt;
            public double I1Seconds, I2Seconds, A1Seconds, A2Seconds, B1Seconds, B2Seconds;
        }
        private static ulong U(IntPtr p) => unchecked((ulong)p.ToInt64());
        private static IntPtr P(ulong p) => new IntPtr(unchecked((long)p));
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool Plausible(Vec v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z) &&
            Math.Abs(v.X) <= CoordinateLimit && Math.Abs(v.Y) <= CoordinateLimit &&
            Math.Abs(v.Z) <= CoordinateLimit && Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) >= 1f;
        private static bool ValidIdle(Vec v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z) &&
            Math.Abs(v.X) <= CoordinateLimit && Math.Abs(v.Y) <= CoordinateLimit && Math.Abs(v.Z) <= CoordinateLimit;
        private static bool StableIdle(Vec a, Vec b) => ValidIdle(a) && ValidIdle(b) &&
            Math.Abs(a.X - b.X) <= StableTolerance && Math.Abs(a.Y - b.Y) <= StableTolerance &&
            Math.Abs(a.Z - b.Z) <= StableTolerance;
        private static bool Stable(Vec a, Vec b) => Plausible(a) && Plausible(b) &&
            Math.Abs(a.X - b.X) <= StableTolerance && Math.Abs(a.Y - b.Y) <= StableTolerance &&
            Math.Abs(a.Z - b.Z) <= StableTolerance;
        private static bool Changed(Vec a, Vec b)
        {
            if (!Plausible(a) || !Plausible(b)) return false;
            double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz >= (double)MinChange * MinChange;
        }
        private static string Distance(Vec value, Vec? player)
        {
            if (!player.HasValue) return "unavailable";
            Vec p = player.Value;
            double x = (double)value.X - p.X, y = (double)value.Y - p.Y, z = (double)value.Z - p.Z;
            return Math.Sqrt(x * x + y * y + z * z).ToString("F1", CultureInfo.InvariantCulture);
        }
        private static Vec VectorAt(byte[] bytes, int i) => new Vec(
            BitConverter.ToSingle(bytes, i), BitConverter.ToSingle(bytes, i + 4), BitConverter.ToSingle(bytes, i + 8));
        private static bool IsWritable(uint protection)
        {
            if ((protection & PageGuard) != 0 || (protection & 0xff) == PageNoAccess) return false;
            uint p = protection & 0xff;
            return p == 0x04 || p == 0x08 || p == 0x40 || p == 0x80;
        }
        private static bool Query(IntPtr process, ulong address, out MemoryBasicInformation mbi)
        {
            return VirtualQueryEx(process, P(address), out mbi,
                new UIntPtr((uint)Marshal.SizeOf<MemoryBasicInformation>())).ToUInt64() != 0;
        }
        private static bool ReadChunk(IntPtr process, Chunk c, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (!Query(process, c.Address, out MemoryBasicInformation m) || m.State != MemCommit ||
                !IsWritable(m.Protect) || m.Type != c.Type || U(m.AllocationBase) != c.AllocationBase)
                return false;
            ulong regionStart = U(m.BaseAddress), regionSize = m.RegionSize.ToUInt64();
            if (c.Address < regionStart || regionSize < (ulong)c.Length ||
                c.Address - regionStart > regionSize - (ulong)c.Length) return false;
            byte[] data = new byte[c.Length];
            if (!ReadProcessMemory(process, P(c.Address), data, new UIntPtr((uint)c.Length), out UIntPtr n) ||
                n.ToUInt64() != (ulong)c.Length) return false;
            bytes = data;
            return true;
        }
        private static byte[]? ReadRaw(IntPtr process, ulong address, int size)
        {
            if (address < 0x10000 || address > MaxUserAddress || size <= 0 ||
                (ulong)size > MaxUserAddress - address) return null;
            byte[] data = new byte[size];
            return ReadProcessMemory(process, P(address), data, new UIntPtr((uint)size), out UIntPtr n) &&
                n.ToUInt64() == (ulong)size ? data : null;
        }
        private static bool Pointer(ulong p) => p >= 0x10000 && p < MaxUserAddress && (p & 7) == 0;
        private static PlayerAnchor? FindPlayer(IntPtr process, ulong imageBase)
        {
            byte[]? guid = ReadRaw(process, imageBase + 0x61C72F0UL, 16);
            byte[]? table = ReadRaw(process, imageBase + 0x582D150UL, 32 * 8);
            if (guid == null || table == null) return null;
            ulong lo = BitConverter.ToUInt64(guid, 0), hi = BitConverter.ToUInt64(guid, 8);
            if ((lo | hi) == 0) return null;
            PlayerAnchor? match = null;
            for (int family = 0; family < 32; family++)
            {
                ulong pool = BitConverter.ToUInt64(table, family * 8);
                if (!Pointer(pool)) continue;
                byte[]? header = ReadRaw(process, pool, 0x18);
                if (header == null) continue;
                ulong elems = BitConverter.ToUInt64(header, 8);
                uint count = BitConverter.ToUInt32(header, 0x10);
                if (!Pointer(elems) || count == 0 || count > 20000) continue;
                byte[]? slots = ReadRaw(process, elems, checked((int)count * 16));
                if (slots == null) continue;
                for (int i = 0; i < (int)count; i++)
                {
                    int offset = i * 16;
                    uint tag = BitConverter.ToUInt32(slots, offset);
                    if ((tag & 0x80000000u) == 0) continue;
                    ulong rec = BitConverter.ToUInt64(slots, offset + 8);
                    if (!Pointer(rec)) continue;
                    byte[]? rg = ReadRaw(process, rec + 0x18, 16);
                    if (rg == null || BitConverter.ToUInt64(rg, 0) != lo || BitConverter.ToUInt64(rg, 8) != hi) continue;
                    // Duplicate matching records are ambiguous; do not choose one.
                    if (match != null) return null;
                    match = new PlayerAnchor { GuidLow = lo, GuidHigh = hi, Record = rec,
                        Slot = elems + (uint)offset, Pool = pool, Elements = elems,
                        Tag = tag, Marker = BitConverter.ToUInt32(slots, offset + 4), Family = family, Index = i };
                }
            }
            if (match == null || !PlayerStillCurrent(process, imageBase, match)) return null;
            byte[]? pointer = ReadRaw(process, match.Record + 0xD8, 8);
            ulong unit = pointer == null ? 0 : BitConverter.ToUInt64(pointer, 0);
            if (unit >= 0x10000 && unit < MaxUserAddress && (unit & 3) == 0)
            {
                byte[]? obj = ReadRaw(process, unit, 0x20);
                if (obj != null && BitConverter.ToUInt64(obj, 0) == imageBase + 0x50A7920UL &&
                    BitConverter.ToUInt64(obj, 8) == match.Record &&
                    BitConverter.ToUInt64(obj, 0x10) == lo && BitConverter.ToUInt64(obj, 0x18) == hi)
                    match.Unit = unit;
            }
            return match;
        }
        private static bool PlayerStillCurrent(IntPtr process, ulong imageBase, PlayerAnchor p)
        {
            byte[]? global = ReadRaw(process, imageBase + 0x61C72F0UL, 16);
            byte[]? pool = ReadRaw(process, imageBase + 0x582D150UL + (uint)p.Family * 8UL, 8);
            byte[]? header = ReadRaw(process, p.Pool + 8, 16);
            byte[]? slot = ReadRaw(process, p.Slot, 16);
            byte[]? guid = ReadRaw(process, p.Record + 0x18, 16);
            return global != null && pool != null && header != null && slot != null && guid != null &&
                BitConverter.ToUInt64(global, 0) == p.GuidLow && BitConverter.ToUInt64(global, 8) == p.GuidHigh &&
                BitConverter.ToUInt64(pool, 0) == p.Pool && BitConverter.ToUInt64(header, 0) == p.Elements &&
                BitConverter.ToUInt32(header, 8) > (uint)p.Index &&
                BitConverter.ToUInt32(slot, 0) == p.Tag && BitConverter.ToUInt32(slot, 4) == p.Marker &&
                BitConverter.ToUInt64(slot, 8) == p.Record &&
                BitConverter.ToUInt64(guid, 0) == p.GuidLow && BitConverter.ToUInt64(guid, 8) == p.GuidHigh;
        }
        private static Vec? PlayerPosition(IntPtr process, ulong imageBase, PlayerAnchor? player)
        {
            if (player == null || !PlayerStillCurrent(process, imageBase, player)) return null;
            byte[]? xyz = ReadRaw(process, player.Record + 0x60, 12);
            if (xyz == null || !PlayerStillCurrent(process, imageBase, player)) return null;
            Vec v = VectorAt(xyz, 0);
            return Plausible(v) ? v : (Vec?)null;
        }
        private static List<Chunk> SelectChunks(IntPtr process, ulong imageBase, ulong imageSize, long limit, Stats stats, PlayerAnchor? player)
        {
            var image = new List<Chunk>();
            var priv = new List<Chunk>();
            ulong cursor = 0, imageEnd = imageBase + imageSize;
            while (cursor < MaxUserAddress && Query(process, cursor, out MemoryBasicInformation m))
            {
                ulong start = U(m.BaseAddress), length = m.RegionSize.ToUInt64();
                if (length == 0 || start > MaxUserAddress || start + length <= start) break;
                ulong end = start + length;
                if (m.State == MemCommit && IsWritable(m.Protect) && (m.Type == MemPrivate || m.Type == MemImage))
                {
                    ulong from = start, to = end;
                    if (m.Type == MemImage)
                    {
                        from = Math.Max(from, imageBase); to = Math.Min(to, imageEnd);
                    }
                    if (to > from)
                    {
                        var destination = m.Type == MemImage ? image : priv;
                        // Windows regions are page-aligned. Partial final chunks are valid.
                        for (ulong at = from; at < to;)
                        {
                            int size = (int)Math.Min((ulong)ChunkSize, to - at);
                            destination.Add(new Chunk { Address = at, Length = size,
                                AllocationBase = U(m.AllocationBase), Type = m.Type });
                            at += (ulong)size;
                        }
                    }
                }
                if (end <= cursor) break;
                cursor = end;
            }
            stats.QueryEndedAt = cursor;
            return ChooseChunks(image, priv, limit, stats, player);
        }
        // Pure selection logic; kept separate to permit synthetic self-tests.
        private static List<Chunk> ChooseChunks(List<Chunk> image, List<Chunk> priv,
            long limit, Stats stats, PlayerAnchor? player)
        {
            // Prioritize small windows around the validated player record and unit,
            // then image pages and private pages spread across the allocation list.
            var selected = new List<Chunk>();
            var used = new HashSet<ulong>();
            Action<Chunk> add = c => {
                if (stats.PlannedBytes + c.Length <= limit && used.Add(c.Address))
                { selected.Add(c); stats.PlannedBytes += c.Length; }
            };
            if (player != null)
            {
                foreach (ulong point in new ulong[] { player.Record, player.Unit })
                {
                    if (point == 0) continue;
                    int index = priv.FindIndex(c => point >= c.Address && point - c.Address < (ulong)c.Length);
                    if (index < 0) continue;
                    for (int delta = -3; delta <= 3; delta++)
                    {
                        int j = index + delta;
                        // Do not call distant, unrelated regions a player window.
                        if (j >= 0 && j < priv.Count && priv[j].AllocationBase == priv[index].AllocationBase)
                            add(priv[j]);
                    }
                }
            }
            stats.AnchorBytes = stats.PlannedBytes;
            // Leave about half the total budget for diverse private pages.
            long imageBudget = Math.Max(stats.PlannedBytes, limit / 2);
            foreach (Chunk c in image)
            {
                if (stats.PlannedBytes + c.Length > imageBudget) break;
                add(c);
            }
            // Stratify by chunk index (not uniform by virtual address). This is
            // partial coverage, not evidence that all allocations were scanned.
            int slots = Math.Min(priv.Count, Math.Max(1, (int)((limit - stats.PlannedBytes) / ChunkSize)));
            for (int i = 0; i < slots && stats.PlannedBytes < limit; i++)
            {
                int index = slots == 1 ? 0 : (int)((long)i * (priv.Count - 1) / (slots - 1));
                add(priv[index]);
            }
            // Fill slack left by short chunks or duplicate selections.
            foreach (Chunk c in priv) add(c);
            foreach (Chunk c in image) add(c);
            foreach (Chunk c in selected) { if (c.Type == MemImage) stats.ImageBytes += c.Length; else stats.PrivateBytes += c.Length; }
            long total = 0;
            foreach (Chunk c in image) total += c.Length;
            foreach (Chunk c in priv) total += c.Length;
            stats.ScopeCapped = stats.PlannedBytes < total;
            return selected;
        }
        private static void Prompt(string message)
        {
            Console.WriteLine(); Console.WriteLine(message);
            Console.WriteLine("Return to this window and press ENTER. Ctrl+C cancels.");
            if (Console.ReadLine() == null) throw new OperationCanceledException("No console input.");
        }
        private static double Capture(IntPtr process, List<Chunk> chunks, Stats stats, int phase)
        {
            var watch = Stopwatch.StartNew();
            foreach (Chunk c in chunks)
            {
                if (c.Dropped) continue;
                if (!ReadChunk(process, c, out byte[] bytes))
                {
                    if (phase == 0) stats.I1Failed++;
                    else if (phase == 1) stats.I2Failed++;
                    else if (phase == 2) stats.A1Failed++;
                    else if (phase == 3) stats.A2Failed++;
                    else if (phase == 4) stats.B1Failed++;
                    else stats.B2Failed++;
                    c.I1 = c.I2 = c.A1 = c.A2 = c.B1 = c.B2 = null;
                    c.Dropped = true;
                    continue;
                }
                if (phase == 0) { c.I1 = bytes; stats.I1Bytes += c.Length; }
                else if (phase == 1) { c.I2 = bytes; stats.I2Bytes += c.Length; }
                else if (phase == 2) { c.A1 = bytes; stats.A1Bytes += c.Length; }
                else if (phase == 3) { c.A2 = bytes; stats.A2Bytes += c.Length; }
                else if (phase == 4) { c.B1 = bytes; stats.B1Bytes += c.Length; }
                else { c.B2 = bytes; stats.B2Bytes += c.Length; }
            }
            watch.Stop();
            Console.WriteLine("{0} captured in {1:F2}s.", new[] { "Idle1", "Idle2", "A1", "A2", "B1", "B2" }[phase], watch.Elapsed.TotalSeconds);
            return watch.Elapsed.TotalSeconds;
        }
        private static List<Candidate> Analyze(List<Chunk> chunks, Stats stats)
        {
            var watch = Stopwatch.StartNew(); var random = new Random(70291);
            var reservoir = new List<Candidate>();
            foreach (Chunk c in chunks)
            {
                if (c.I1 == null || c.I2 == null || c.A1 == null || c.A2 == null || c.B1 == null || c.B2 == null) continue;
                stats.RetainedChunks++;
                int slots = c.Length < 12 ? 0 : (c.Length - 12) / 4 + 1;
                for (int i = 0; i < slots; i++)
                {
                    int offset = i * 4;
                    Vec idle = VectorAt(c.I1, offset);
                    if (!StableIdle(idle, VectorAt(c.I2, offset))) continue;
                    Vec a = VectorAt(c.A1, offset), a2 = VectorAt(c.A2, offset);
                    if (!Stable(a, a2)) continue;
                    stats.StableA++;
                    Vec b = VectorAt(c.B1, offset);
                    if (!Changed(a, b)) continue;
                    stats.ChangedAB++;
                    if (!Stable(b, VectorAt(c.B2, offset))) continue;
                    stats.StableB++;
                    // Uniform sampling avoids silently preferring the first heap pages.
                    long n = stats.StableB;
                    long pick = n <= MaxOutput ? n - 1 : random.NextInt64(n);
                    if (pick < MaxOutput)
                    {
                        var candidate = new Candidate { Chunk = c, Offset = offset, Idle = idle, A = a, B = b };
                        if (n <= MaxOutput) reservoir.Add(candidate);
                        else reservoir[(int)pick] = candidate;
                    }
                }
                c.I1 = c.I2 = c.A1 = c.A2 = c.B1 = c.B2 = null;
            }
            watch.Stop(); Console.WriteLine("Analysis complete in {0:F1}s; matching triples={1:N0}.", watch.Elapsed.TotalSeconds, stats.StableB);
            reservoir.Sort((a, b) => (a.Chunk.Address + (uint)a.Offset).CompareTo(b.Chunk.Address + (uint)b.Offset));
            return reservoir;
        }
        private static void PutVec(byte[] bytes, int i, Vec v)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(v.X), 0, bytes, i, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(v.Y), 0, bytes, i + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(v.Z), 0, bytes, i + 8, 4);
        }
        private static int SelfTest()
        {
            Vec a = new Vec(100, 200, 30), nearby = new Vec(100.02f, 200, 30), b = new Vec(110, 205, 30);
            bool ok = Stable(a, nearby) && !Stable(a, b) && Changed(a, b) && !Changed(a, nearby) &&
                !Plausible(new Vec(float.NaN, 1, 1)) && !Plausible(new Vec(float.PositiveInfinity, 1, 1)) &&
                !Plausible(new Vec(0, 0, 0)) && !Plausible(new Vec(40001, 2, 3));
            var chunk = new Chunk { Address = 0x100000, Length = 64,
                I1 = new byte[64], I2 = new byte[64], A1 = new byte[64], A2 = new byte[64], B1 = new byte[64], B2 = new byte[64] };
            PutVec(chunk.A1, 16, a); PutVec(chunk.A2, 16, nearby);
            PutVec(chunk.B1, 16, b); PutVec(chunk.B2, 16, b);
            var skipped = new Chunk { Address = 0x200000, Length = 64, A1 = new byte[64] };
            var stats = new Stats();
            List<Candidate> candidates = Analyze(new List<Chunk> { chunk, skipped }, stats);
            bool found = false;
            foreach (Candidate c in candidates) if (c.Chunk.Address == 0x100000 && c.Offset == 16) found = true;
            ok = ok && found && stats.RetainedChunks == 1 && stats.StableB >= 1;
            var syntheticPrivate = new List<Chunk>();
            for (int i = 0; i < 1000; i++)
                syntheticPrivate.Add(new Chunk { Address = 0x100000UL + (ulong)i * ChunkSize,
                    Length = ChunkSize, AllocationBase = 0x100000UL, Type = MemPrivate });
            var choiceStats = new Stats();
            var anchor = new PlayerAnchor { Record = syntheticPrivate[960].Address };
            List<Chunk> selected = ChooseChunks(new List<Chunk>(), syntheticPrivate, 64L * ChunkSize, choiceStats, anchor);
            bool hasAnchor = false, nearStart = false, nearEnd = false;
            var seen = new HashSet<ulong>();
            foreach (Chunk c in selected)
            {
                if (c.Address == anchor.Record) hasAnchor = true;
                if (c.Address == syntheticPrivate[0].Address) nearStart = true;
                if (c.Address == syntheticPrivate[999].Address) nearEnd = true;
                if (!seen.Add(c.Address)) ok = false;
            }
            ok = ok && hasAnchor && nearStart && nearEnd && choiceStats.ScopeCapped && choiceStats.PlannedBytes <= 64L * ChunkSize;
            Console.WriteLine(ok ? "Self-test passed (offline comparison logic only)." : "Self-test FAILED.");
            return ok ? 0 : 1;
        }
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--selftest") return SelfTest();
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            { Console.Error.WriteLine("Requires 64-bit Windows and an x64 process."); return 2; }
            if (args.Length > 2 || (args.Length > 0 && args[0] != "--pid"))
            { Console.Error.WriteLine("Usage: WowBCtmCapture [--pid PID] or --selftest"); return 2; }
            int pid = 0;
            if (args.Length == 2 && (!int.TryParse(args[1], out pid) || pid <= 0))
            { Console.Error.WriteLine("Invalid --pid."); return 2; }
            if (args.Length == 1) { Console.Error.WriteLine("--pid requires a number."); return 2; }
            if (pid == 0)
            {
                Process[] found = Process.GetProcessesByName("WowB");
                if (found.Length != 1)
                { Console.Error.WriteLine("Expected exactly one WowB process; use --pid PID if needed."); return 2; }
                pid = found[0].Id;
                foreach (Process p in found) p.Dispose();
            }
            IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (process == IntPtr.Zero)
            { Console.Error.WriteLine("OpenProcess failed: " + Marshal.GetLastWin32Error()); return 1; }
            try
            {
                using Process target = Process.GetProcessById(pid);
                if (!string.Equals(target.ProcessName, "WowB", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Target must be WowB.exe.");
                using ProcessModule module = target.MainModule ?? throw new InvalidOperationException("Main module unavailable.");
                ulong imageBase = U(module.BaseAddress), imageSize = (ulong)module.ModuleMemorySize;
                // Executable headers are commonly read-only; do not apply writable-page filter here.
                byte[] mz = new byte[64];
                if (!ReadProcessMemory(process, P(imageBase), mz, new UIntPtr(64), out UIntPtr n) || n.ToUInt64() != 64 ||
                    mz[0] != (byte)'M' || mz[1] != (byte)'Z') throw new InvalidOperationException("Unreadable PE header.");
                int pe = BitConverter.ToInt32(mz, 0x3c);
                if (pe < 64 || pe > 1024 * 1024) throw new InvalidOperationException("Invalid PE offset.");
                byte[] nt = new byte[6];
                if (!ReadProcessMemory(process, P(imageBase + (uint)pe), nt, new UIntPtr(6), out n) || n.ToUInt64() != 6 ||
                    BitConverter.ToUInt32(nt, 0) != 0x00004550 || BitConverter.ToUInt16(nt, 4) != 0x8664)
                    throw new InvalidOperationException("Target is not a readable AMD64 PE.");
                byte[] identity = new byte[16];
                string identityText = ReadProcessMemory(process, P(imageBase + 0x1D63190UL), identity,
                    new UIntPtr(16), out n) && n.ToUInt64() == 16 ? Convert.ToHexString(identity) : "unreadable";
                string dir = @"C:\ghidra-scripts"; Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "WowBCtmCapture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
                var stats = new Stats(); const long limit = 128L * 1024 * 1024;
                PlayerAnchor? player = FindPlayer(process, imageBase);
                List<Chunk> chunks = SelectChunks(process, imageBase, imageSize, limit, stats, player);
                if (chunks.Count == 0) throw new InvalidOperationException("No writable image/private chunks selected.");
                Console.WriteLine("READ ONLY: no writes, injections, process suspension or gameplay calls.");
                Console.WriteLine("Selected {0:N0} MiB in {1} chunks; cap={2}. Short/failed reads are skipped.",
                    stats.PlannedBytes / 1048576.0, chunks.Count, stats.ScopeCapped);
                Console.WriteLine("Captures are non-atomic and can use roughly 768 MiB of memory. Keep the SAME process and character in-world throughout.");
                Console.WriteLine(player == null ? "Player anchor unavailable; scan falls back to broad sampling." :
                    "Player record verified by GUID/slot checks; snapshot still non-atomic.");
                Console.WriteLine("Choose a safe open area; click far enough away that the character is still walking when each scan finishes.");
                Prompt("STEP 0: stay idle, without moving, and leave the character in-world.");
                stats.I1Seconds = Capture(process, chunks, stats, 0); Vec? idle1 = PlayerPosition(process, imageBase, player);
                Thread.Sleep(500); stats.I2Seconds = Capture(process, chunks, stats, 1); Vec? idle2 = PlayerPosition(process, imageBase, player);
                Prompt("STEP A: manually right-click ground destination A, then immediately return here.");
                stats.A1Seconds = Capture(process, chunks, stats, 2); Vec? a1 = PlayerPosition(process, imageBase, player);
                Thread.Sleep(500); stats.A2Seconds = Capture(process, chunks, stats, 3); Vec? a2 = PlayerPosition(process, imageBase, player);
                Prompt("STEP B: manually right-click a DIFFERENT ground destination B, then immediately return here.");
                stats.B1Seconds = Capture(process, chunks, stats, 4); Vec? b1 = PlayerPosition(process, imageBase, player);
                Thread.Sleep(500); stats.B2Seconds = Capture(process, chunks, stats, 5); Vec? b2 = PlayerPosition(process, imageBase, player);
                var candidates = Analyze(chunks, stats);
                var sb = new StringBuilder();
                sb.AppendLine("WOWB CTM DIFFERENTIAL CAPTURE v2 -- RESEARCH LEADS ONLY");
                sb.AppendLine("No process writes/injection. Captures are non-atomic; values may be unrelated, transient, or stale.");
                sb.AppendLine("Method: idle1/idle2 stable (0.05 each), A1/A2 stable (0.05 each), B1 differs by >=3, B1/B2 stable (0.05 each); finite aligned float triples.");
                sb.AppendLine("Not a CTM offset, validated destination, or ABI. Idle is a control; no identical-destination repeat or independent movement control included.");
                sb.AppendLine("Candidate triples crossing 1-MiB chunk boundaries, unaligned triples and other formats are omitted.");
                sb.AppendLine("pid=" + pid + " imageBase=0x" + imageBase.ToString("X") + " imageSize=0x" + imageSize.ToString("X"));
                sb.AppendLine("UnitHealth sample RVA 0x1D63190=" + identityText + " expected=488BC4554157488D68A14881ECB80000 (partial identity only; may be encrypted)");
                sb.AppendLine("Selected private chunks near player record/unit when validated, then main-image and stratified private chunks; no mapped/readonly memory.");
                sb.AppendLine(player == null ? "playerAnchor=unavailable" : "playerAnchor=GUID-slot-checked-nonatomic family=" + player.Family + " slot=" + player.Index +
                    " record=0x" + player.Record.ToString("X") + " unit=" + (player.Unit == 0 ? "unavailable" : "0x" + player.Unit.ToString("X")));
                sb.AppendLine("player positions sampled AFTER each scan (not atomic): idle1=" + (idle1?.ToString() ?? "unavailable") +
                    " idle2=" + (idle2?.ToString() ?? "unavailable") + " A1=" + (a1?.ToString() ?? "unavailable") +
                    " A2=" + (a2?.ToString() ?? "unavailable") + " B1=" + (b1?.ToString() ?? "unavailable") + " B2=" + (b2?.ToString() ?? "unavailable"));
                sb.AppendLine("Byte cap=" + limit + " planned=" + stats.PlannedBytes + " scopeCapped=" + stats.ScopeCapped +
                    " chunks=" + chunks.Count + " imageBytes=" + stats.ImageBytes + " privateBytes=" + stats.PrivateBytes + " anchorWindowBytes=" + stats.AnchorBytes + " idle1=" + stats.I1Bytes + " idle2=" + stats.I2Bytes + " scannedA1=" + stats.A1Bytes + " A2=" + stats.A2Bytes +
                    " B1=" + stats.B1Bytes + " B2=" + stats.B2Bytes);
                sb.AppendLine("Failed chunks idle1=" + stats.I1Failed + " idle2=" + stats.I2Failed + " A1=" + stats.A1Failed + " A2=" + stats.A2Failed + " B1=" + stats.B1Failed + " B2=" + stats.B2Failed);
                sb.AppendLine("Capture seconds idle1=" + stats.I1Seconds.ToString("F2", CultureInfo.InvariantCulture) + " idle2=" + stats.I2Seconds.ToString("F2", CultureInfo.InvariantCulture) + " A1=" + stats.A1Seconds.ToString("F2", CultureInfo.InvariantCulture) + " A2=" + stats.A2Seconds.ToString("F2", CultureInfo.InvariantCulture) +
                    " B1=" + stats.B1Seconds.ToString("F2", CultureInfo.InvariantCulture) + " B2=" + stats.B2Seconds.ToString("F2", CultureInfo.InvariantCulture));
                sb.AppendLine("queryEndedAt=0x" + stats.QueryEndedAt.ToString("X") + " fullyReadChunks=" + stats.RetainedChunks);
                sb.AppendLine("A-stable=" + stats.StableA + " AB-changed=" + stats.ChangedAB + " B-stable=" + stats.StableB +
                    " sampled=" + candidates.Count + " outputCapped=" + (stats.StableB > MaxOutput));
                sb.AppendLine("Uniform random sample if capped; results include client addresses and values. Review before sharing.");
                foreach (Candidate item in candidates)
                {
                    Chunk c = item.Chunk; ulong address = c.Address + (uint)item.Offset;
                    string kind = c.Type == MemImage ? "image+rva=0x" + (address - imageBase).ToString("X") : "private";
                    sb.AppendLine("candidate VA=0x" + address.ToString("X") + " " + kind +
                        " alloc=0x" + c.AllocationBase.ToString("X") + " idle=" + item.Idle + " A=" + item.A + " B=" + item.B +
                        " approxDistToPlayerA1=" + Distance(item.A, a1) + " approxDistToPlayerB1=" + Distance(item.B, b1));
                }
                sb.AppendLine("shown=" + candidates.Count);
                sb.AppendLine("=== COMPLETE (bounds and partial reads apply) ===");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Console.WriteLine("Report saved: " + path);
                Console.WriteLine("Upload only this report. Zero matches do not rule out CTM.");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("Capture failed: " + e.Message); return 1; }
            finally { CloseHandle(process); }
        }
    }
}
