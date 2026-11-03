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

        public Dictionary<string, uint> GetAllPatterns()
        {
            return this._patterns;
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
            uint start = (uint)((ulong)peHeaderParser.ModulePtr.ToInt32() + (ulong)peHeaderParser.NtHeader.OptionalHeader.BaseOfCode);
            uint length = peHeaderParser.NtHeader.OptionalHeader.BaseOfData - 2U - peHeaderParser.NtHeader.OptionalHeader.BaseOfCode;
            this.LoadFile(file, start, length);
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
