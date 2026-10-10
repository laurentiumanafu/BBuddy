using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using GreenMagic.Native;

namespace GreenMagic.Internals
{
    public class PatternManager
    {
        private readonly Dictionary<string, uint> _patterns = new Dictionary<string, uint>();
        private readonly Dictionary<string, ulong> _patterns64 = new Dictionary<string, ulong>();

        private readonly Memory _memory;

        static PatternManager()
        {
        }

        public PatternManager(Memory memory)
        {
            this._memory = memory;
        }

        public uint this[string name]
        {
            get
            {
                return this._patterns[name];
            }
        }

        /// <summary>
        /// Absolute address of a pattern resolved by <see cref="LoadFile64"/>.
        /// </summary>
        public ulong GetPattern64(string name)
        {
            return this._patterns64[name];
        }

        public Dictionary<string, uint> GetAllPatterns()
        {
            return this._patterns;
        }

        public Dictionary<string, ulong> GetAllPatterns64()
        {
            return this._patterns64;
        }

        public void LoadFile(string file, uint start, uint length)
        {
            if (string.IsNullOrEmpty(file))
            {
                throw new ArgumentNullException("file");
            }
            if (start == 0U)
            {
                throw new ArgumentOutOfRangeException("start", "Start address cannot be 0!");
            }
            if (length == 0U)
            {
                throw new ArgumentOutOfRangeException("length", "Length cannot be 0!");
            }
            this.LoadFile(XElement.Load(file), this._memory.ReadBytes(start, (int)length), start);
        }

        public void LoadFile(string file, IntPtr hModule)
        {
            if (hModule == IntPtr.Zero)
            {
                throw new ArgumentException("hModule cannot be 0!", "hModule");
            }
            PeHeaderParser peHeaderParser = new PeHeaderParser(hModule, this._memory);

            // PE32+ (x64) images have no BaseOfData member, so the classic
            // BaseOfCode..BaseOfData range only exists in PE32 headers.
            // For 64-bit targets (and for Large-Address-Aware 32-bit images
            // whose span may exceed 2 GB) derive the range from the
            // section headers instead.
            if (peHeaderParser.Is64BitImage || this._memory.Is64BitTarget)
            {
                ulong start;
                ulong length;
                if (this._memory.Is64BitTarget)
                {
                    this.GetScanRange64(peHeaderParser, out start, out length);
                    this.LoadFile64(file, start, length);
                }
                else
                {
                    this.GetScanRange64(peHeaderParser, out start, out length);
                    if (start > uint.MaxValue || start + length > uint.MaxValue)
                    {
                        throw new InvalidOperationException("Scan range does not fit in a 32-bit address.");
                    }
                    this.LoadFile(file, (uint)start, (uint)length);
                }
                return;
            }

            uint start32 = (uint)((ulong)peHeaderParser.ModulePtr.ToInt32() + (ulong)peHeaderParser.NtHeader.OptionalHeader.BaseOfCode);
            uint length32 = peHeaderParser.NtHeader.OptionalHeader.BaseOfData - 2U - peHeaderParser.NtHeader.OptionalHeader.BaseOfCode;
            this.LoadFile(file, start32, length32);
        }

        public void LoadFile(string file)
        {
            this.LoadFile(file, this._memory.Process.Modules[0].BaseAddress);
        }

        private void LoadFile(XContainer file, byte[] data, uint start)
        {
            IEnumerable<XElement> enumerable = from p in file.Descendants("Pattern")
            select p;
            foreach (XElement xelement in enumerable)
            {
                uint searchStartOffset = 0U;
                string value = xelement.Attribute("desc").Value;
                string value2 = xelement.Attribute("mask").Value;
                byte[] bytesFromPattern = PatternManager.GetBytesFromPattern(xelement.Attribute("pattern").Value);
                if (value2.Length != bytesFromPattern.Length)
                {
                    throw new Exception("Pattern and mask lengths do not match!");
                }
                if (xelement.Attribute("start") != null)
                {
                    searchStartOffset = this[xelement.Attribute("start").Value] - start + 1U;
                    if ((ulong)searchStartOffset > (ulong)((long)data.Length))
                    {
                        continue;
                    }
                }
                uint patternAddress = PatternManager.Find(data, value2, bytesFromPattern, searchStartOffset);
                if (patternAddress == 0U)
                {
                    throw new Exception("FindPattern failed... figure it out ****tard!");
                }
                foreach (XElement xelement2 in xelement.Elements())
                {
                    string localName = xelement2.Name.LocalName;
                    if (localName != null)
                    {
                        if (localName == "Lea")
                        {
                            patternAddress = BitConverter.ToUInt32(data, (int)patternAddress);
                            start = 0U;
                        }
                        else if (localName == "Rel")
                        {
                            int instructionSize = int.Parse(xelement2.Attribute("size").Value, NumberStyles.HexNumber);
                            int relativeOffset = int.Parse(xelement2.Attribute("offset").Value, NumberStyles.HexNumber);
                            patternAddress = (uint)((ulong)(BitConverter.ToUInt32(data, (int)patternAddress) + patternAddress) + (ulong)instructionSize - (ulong)relativeOffset);
                        }
                        else if (localName == "Add")
                        {
                            patternAddress += uint.Parse(xelement2.Attribute("value").Value, NumberStyles.HexNumber);
                        }
                        else if (localName == "Sub")
                        {
                            patternAddress -= uint.Parse(xelement2.Attribute("value").Value, NumberStyles.HexNumber);
                        }
                    }
                }
                if (!this._patterns.ContainsKey(value))
                {
                    this._patterns.Add(value, patternAddress + start);
                }
            }
        }

        /// <summary>
        /// Derives the scannable virtual range of a module from its PE
        /// section headers. Works for PE32+ (which has no BaseOfData) and
        /// for any image whose span exceeds what the classic
        /// BaseOfCode..BaseOfData arithmetic can express.
        /// </summary>
        private void GetScanRange64(PeHeaderParser peHeaderParser, out ulong start, out ulong length)
        {
            ulong moduleAddress = (ulong)peHeaderParser.ModulePtr.ToInt64();
            var fileHeader = peHeaderParser.Is64BitImage
                ? peHeaderParser.NtHeader64.FileHeader
                : peHeaderParser.NtHeader.FileHeader;

            ulong ntAddress = moduleAddress + (uint)peHeaderParser.DosHeader.e_lfanew;
            ulong sectionsAddress = ntAddress + 4UL + 20UL + fileHeader.SizeOfOptionalHeader; // signature + file header + optional header

            const int sectionHeaderSize = 40; // IMAGE_SECTION_HEADER
            byte[] sections = this._memory.ReadBytes64(sectionsAddress, fileHeader.NumberOfSections * sectionHeaderSize);
            if (sections == null)
                throw new Exception("Failed to read PE section headers for pattern scanning.");

            const uint IMAGE_SCN_CNT_CODE = 0x00000020u;
            const uint IMAGE_SCN_CNT_INITIALIZED_DATA = 0x00000040u;

            ulong minVa = ulong.MaxValue;
            ulong maxEnd = 0UL;
            for (int i = 0; i < fileHeader.NumberOfSections; i++)
            {
                int o = i * sectionHeaderSize;
                uint virtualSize = BitConverter.ToUInt32(sections, o + 8);
                uint virtualAddress = BitConverter.ToUInt32(sections, o + 12);
                uint characteristics = BitConverter.ToUInt32(sections, o + 36);

                if (virtualSize == 0U || (characteristics & (IMAGE_SCN_CNT_CODE | IMAGE_SCN_CNT_INITIALIZED_DATA)) == 0U)
                    continue;

                if (virtualAddress < minVa)
                    minVa = virtualAddress;

                ulong end = (ulong)virtualAddress + virtualSize;
                if (end > maxEnd)
                    maxEnd = end;
            }

            if (minVa == ulong.MaxValue || maxEnd == 0UL)
                throw new Exception("No scannable PE sections found.");

            start = moduleAddress + minVa;
            length = maxEnd - minVa;
        }

        /// <summary>
        /// 64-bit variant of <see cref="LoadFile(string, ulong, ulong)"/>:
        /// reads the module range with <see cref="Memory.ReadBytes64"/> and
        /// resolves patterns to absolute 64-bit addresses. The Pattern XML
        /// semantics (mask/pattern, Lea, Rel, Add, Sub) are unchanged; Rel
        /// still reads a 32-bit displacement, which is also what x64
        /// RIP-relative instructions encode.
        /// </summary>
        public void LoadFile64(string file, ulong start, ulong length)
        {
            if (string.IsNullOrEmpty(file))
                throw new ArgumentNullException("file");
            if (start == 0UL)
                throw new ArgumentOutOfRangeException("start", "Start address cannot be 0!");
            if (length == 0UL)
                throw new ArgumentOutOfRangeException("length", "Length cannot be 0!");
            if (length > int.MaxValue)
                throw new ArgumentOutOfRangeException("length", "Scan range too large for a single pattern pass.");

            this.LoadFile64(XElement.Load(file), this._memory.ReadBytes64(start, (int)length), start);
        }

        private void LoadFile64(XContainer file, byte[] data, ulong start)
        {
            IEnumerable<XElement> enumerable = from p in file.Descendants("Pattern")
                                               select p;
            foreach (XElement xelement in enumerable)
            {
                ulong searchStartOffset = 0UL;
                string value = xelement.Attribute("desc").Value;
                string value2 = xelement.Attribute("mask").Value;
                byte[] bytesFromPattern = PatternManager.GetBytesFromPattern(xelement.Attribute("pattern").Value);
                if (value2.Length != bytesFromPattern.Length)
                {
                    throw new Exception("Pattern and mask lengths do not match!");
                }
                if (xelement.Attribute("start") != null)
                {
                    string startRef = xelement.Attribute("start").Value;
                    ulong startRefAddress;
                    if (this._patterns64.TryGetValue(startRef, out ulong a64))
                        startRefAddress = a64;
                    else if (this._patterns.TryGetValue(startRef, out uint a32))
                        startRefAddress = a32;
                    else
                        throw new Exception($"Unknown pattern reference '{startRef}'");

                    searchStartOffset = startRefAddress - start + 1UL;
                    if (searchStartOffset > (ulong)((long)data.Length))
                    {
                        continue;
                    }
                }
                ulong patternAddress = PatternManager.Find64(data, value2, bytesFromPattern, searchStartOffset);
                if (patternAddress == 0UL)
                {
                    throw new Exception("FindPattern failed... figure it out ****tard!");
                }
                foreach (XElement xelement2 in xelement.Elements())
                {
                    string localName = xelement2.Name.LocalName;
                    if (localName != null)
                    {
                        if (localName == "Lea")
                        {
                            // The match itself carries a 32-bit word
                            // (x86 absolute, or x64 RIP-relative handled
                            // via Rel instead).
                            patternAddress = BitConverter.ToUInt32(data, (int)patternAddress);
                            start = 0UL;
                        }
                        else if (localName == "Rel")
                        {
                            // rel32 displacement — identical encoding for
                            // x86 and x64 RIP-relative operands.
                            int instructionSize = int.Parse(xelement2.Attribute("size").Value, NumberStyles.HexNumber);
                            int relativeOffset = int.Parse(xelement2.Attribute("offset").Value, NumberStyles.HexNumber);
                            int rel32 = BitConverter.ToInt32(data, (int)patternAddress);
                            patternAddress = (ulong)((long)patternAddress + rel32 + instructionSize - relativeOffset);
                        }
                        else if (localName == "Add")
                        {
                            patternAddress += ulong.Parse(xelement2.Attribute("value").Value, NumberStyles.HexNumber);
                        }
                        else if (localName == "Sub")
                        {
                            patternAddress -= ulong.Parse(xelement2.Attribute("value").Value, NumberStyles.HexNumber);
                        }
                    }
                }
                if (!this._patterns64.ContainsKey(value))
                {
                    this._patterns64.Add(value, patternAddress + start);
                }
            }
        }

        private static byte[] GetBytesFromPattern(string pattern)
        {
            char[] separator = new char[]
            {
                '\\',
                'x'
            };
            string[] array = pattern.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            byte[] array2 = new byte[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                array2[i] = byte.Parse(array[i], NumberStyles.HexNumber);
            }
            return array2;
        }

        private static uint Find(byte[] data, string mask, byte[] byteMask, uint start)
        {
            uint currentOffset = start;
            while ((ulong)currentOffset < (ulong)((long)data.Length))
            {
                if (PatternManager.DataCompare(data, (int)currentOffset, byteMask, mask))
                {
                    return currentOffset;
                }
                currentOffset += 1U;
            }
            return 0U;
        }

        private static ulong Find64(byte[] data, string mask, byte[] byteMask, ulong start)
        {
            ulong currentOffset = start;
            while (currentOffset < (ulong)((long)data.Length))
            {
                if (PatternManager.DataCompare(data, (int)currentOffset, byteMask, mask))
                {
                    return currentOffset;
                }
                currentOffset += 1UL;
            }
            return 0UL;
        }

        private static bool DataCompare(byte[] data, int offset, byte[] byteMask, string mask)
        {
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] == 'x' && byteMask[i] != data[i + offset])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
