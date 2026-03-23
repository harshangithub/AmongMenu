using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AmongMenu.Utilities
{
    /// <summary>
    /// Scans the Among Us process memory for IL2CPP static-field pointers using
    /// byte-signature (AOB) scanning.
    ///
    /// How it works
    /// ─────────────
    /// IL2CPP x64 accesses a static field through a pointer stored in the module's
    /// data segment.  The generated accessor stub typically contains an instruction
    /// of the form:
    ///
    ///   MOV RAX, [RIP + disp32]   ;  48 8B 05 &lt;d0 d1 d2 d3&gt;
    ///
    /// where the 4-byte little-endian displacement, relative to the next
    /// instruction, gives the address of the static-field storage slot.
    ///
    /// <see cref="FindPattern"/> scans the loaded GameAssembly.dll image for a
    /// caller-supplied byte pattern (with '??' wildcards).  For each match
    /// <see cref="ResolveRipRelative"/> extracts the displacement and computes the
    /// resulting module RVA, which can then be placed in <c>offsets.json</c>.
    ///
    /// Usage
    /// ──────
    /// <code>
    /// var scanner = new OffsetScanner(handle, moduleBase, moduleSize);
    /// byte?[] pattern = OffsetScanner.ParsePattern("48 8B 05 ?? ?? ?? ??");
    /// foreach (long rva in scanner.FindPatternRvas(pattern, ripDisp: 3, instrSize: 7))
    ///     Debug.WriteLine($"Candidate GameData RVA: 0x{rva:X}");
    /// </code>
    /// </summary>
    public class OffsetScanner
    {
        // ── Win32 ─────────────────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            byte[] lpBuffer,
            int dwSize,
            out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer,
            int dwLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint   AllocationProtect;
            public IntPtr RegionSize;
            public uint   State;
            public uint   Protect;
            public uint   Type;
        }

        private const uint MEM_COMMIT          = 0x1000;
        private const uint PAGE_EXECUTE_READ   = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_READONLY       = 0x02;
        private const uint PAGE_READWRITE      = 0x04;

        // Maximum chunk size read per VirtualQueryEx region (4 MB)
        private const int MaxChunkSize = 4 * 1024 * 1024;

        // ── State ─────────────────────────────────────────────────────────────

        private readonly IntPtr _processHandle;
        private readonly long   _moduleBase;
        private readonly int    _moduleSize;

        /// <summary>
        /// Initialises the scanner.
        /// </summary>
        /// <param name="processHandle">An open process handle with VM_READ rights.</param>
        /// <param name="moduleBase">The base address of <c>GameAssembly.dll</c> in the target process.</param>
        /// <param name="moduleSize">The size of the module image in bytes.</param>
        public OffsetScanner(IntPtr processHandle, long moduleBase, int moduleSize)
        {
            _processHandle = processHandle;
            _moduleBase    = moduleBase;
            _moduleSize    = moduleSize;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a human-readable hex byte string into a nullable byte array
        /// suitable for <see cref="FindPatternRvas"/>.
        /// Use <c>??</c> (or <c>?</c>) as a wildcard for "any byte".
        /// </summary>
        /// <example>"48 8B 05 ?? ?? ?? ??"</example>
        public static byte?[] ParsePattern(string hexString)
        {
            var tokens = hexString.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new byte?[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                result[i] = (t == "??" || t == "?") ? (byte?)null : Convert.ToByte(t, 16);
            }
            return result;
        }

        /// <summary>
        /// Scans the module for all occurrences of <paramref name="pattern"/> and,
        /// for each match, treats the 4-byte value at
        /// <c>match + <paramref name="ripDisplacement"/></c> as a RIP-relative
        /// displacement and returns the resulting module RVA.
        ///
        /// This is the primary entry point for locating IL2CPP static-field pointers.
        /// </summary>
        /// <param name="pattern">Byte pattern (nulls are wildcards).</param>
        /// <param name="ripDisplacement">Byte offset within the matched instruction where the 4-byte displacement starts.</param>
        /// <param name="instrSize">Total length of the matched instruction (used to compute RIP+1 address).</param>
        /// <returns>Sequence of RVAs; empty when nothing is found.</returns>
        public IEnumerable<long> FindPatternRvas(byte?[] pattern, int ripDisplacement, int instrSize)
        {
            foreach (long absoluteMatchAddr in ScanModuleForPattern(pattern))
            {
                long? rva = ResolveRipRelative(absoluteMatchAddr, ripDisplacement, instrSize);
                if (rva.HasValue)
                    yield return rva.Value;
            }
        }

        /// <summary>
        /// Scans the module for the pattern and returns the raw match addresses
        /// (absolute addresses in the target process address space).
        /// Useful for further manual analysis.
        /// </summary>
        public IEnumerable<long> FindPattern(byte?[] pattern)
        {
            return ScanModuleForPattern(pattern);
        }

        /// <summary>
        /// Reads the 4-byte RIP-relative displacement at
        /// <c>instructionAddress + ripDisplacementOffset</c>, adds the
        /// instruction size, and returns the resulting module RVA.
        /// Returns <c>null</c> if the read fails or the result falls outside
        /// the module.
        /// </summary>
        public long? ResolveRipRelative(long instructionAddress, int ripDisplacementOffset, int instrSize)
        {
            IntPtr dispAddr = (IntPtr)(instructionAddress + ripDisplacementOffset);
            byte[] buf = new byte[4];
            if (!ReadProcessMemory(_processHandle, dispAddr, buf, 4, out int bytesRead) || bytesRead < 4)
                return null;

            int disp32 = BitConverter.ToInt32(buf, 0);
            // The displacement is relative to the address of the *next* instruction
            long targetAbsolute = instructionAddress + instrSize + disp32;
            long rva = targetAbsolute - _moduleBase;

            // Sanity: RVA must be within the module
            if (rva < 0 || rva >= _moduleSize)
                return null;

            return rva;
        }

        /// <summary>
        /// Produces a multi-line diagnostic string describing every pattern
        /// match found during a scan.  Useful for debug logging.
        /// </summary>
        public string Diagnose(string patternHex, int ripDisplacement, int instrSize)
        {
            var sb = new StringBuilder();
            byte?[] pattern = ParsePattern(patternHex);
            sb.AppendLine($"Scanning for: {patternHex}");
            int count = 0;

            foreach (long absAddr in ScanModuleForPattern(pattern))
            {
                long rva   = absAddr - _moduleBase;
                long? resolved = ResolveRipRelative(absAddr, ripDisplacement, instrSize);
                sb.AppendLine(resolved.HasValue
                    ? $"  Match @ RVA 0x{rva:X8}  →  target RVA 0x{resolved.Value:X8}"
                    : $"  Match @ RVA 0x{rva:X8}  →  resolve failed");
                count++;
                if (count >= 20) { sb.AppendLine("  … (capped at 20 results)"); break; }
            }

            if (count == 0)
                sb.AppendLine("  No matches found.");

            return sb.ToString();
        }

        // ── Private scanning ──────────────────────────────────────────────────

        /// <summary>
        /// Enumerates every readable, committed page within the module range and
        /// searches each for the given byte pattern.
        /// </summary>
        private IEnumerable<long> ScanModuleForPattern(byte?[] pattern)
        {
            long addr    = _moduleBase;
            long modEnd  = _moduleBase + _moduleSize;

            while (addr < modEnd)
            {
                int queryResult = VirtualQueryEx(_processHandle, (IntPtr)addr,
                    out MEMORY_BASIC_INFORMATION mbi,
                    Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));

                if (queryResult == 0)
                    break;

                long regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0)
                    break;

                long regionEnd = addr + regionSize;

                if (mbi.State == MEM_COMMIT && IsReadablePage(mbi.Protect))
                {
                    // Clamp to module bounds
                    long readStart = addr;
                    long readEnd   = Math.Min(regionEnd, modEnd);
                    int  chunkSize = (int)Math.Min(readEnd - readStart, MaxChunkSize);

                    byte[] buf = new byte[chunkSize];
                    if (ReadProcessMemory(_processHandle, (IntPtr)readStart, buf, chunkSize, out int bytesRead)
                        && bytesRead > 0)
                    {
                        foreach (int offset in SearchBuffer(buf, bytesRead, pattern))
                            yield return readStart + offset;
                    }
                }

                addr = regionEnd;
            }
        }

        private static bool IsReadablePage(uint protect)
        {
            // Ignore guard, no-access, etc.
            const uint PAGE_GUARD    = 0x100;
            const uint PAGE_NOACCESS = 0x01;
            if ((protect & PAGE_GUARD) != 0 || protect == PAGE_NOACCESS)
                return false;

            return (protect & (PAGE_READONLY | PAGE_READWRITE |
                               PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) != 0;
        }

        /// <summary>
        /// Boyer-Moore-Horspool-inspired search within a byte buffer.
        /// Returns all starting offsets where the pattern matches.
        /// </summary>
        private static IEnumerable<int> SearchBuffer(byte[] buffer, int length, byte?[] pattern)
        {
            int patLen = pattern.Length;
            if (patLen == 0 || length < patLen)
                yield break;

            for (int i = 0; i <= length - patLen; i++)
            {
                bool match = true;
                for (int j = 0; j < patLen; j++)
                {
                    if (pattern[j].HasValue && buffer[i + j] != pattern[j]!.Value)
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    yield return i;
            }
        }
    }
}
