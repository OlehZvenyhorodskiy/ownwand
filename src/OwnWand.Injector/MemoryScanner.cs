using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OwnWand.Injector;

public static class MemoryScanner
{
    // Win32 API Imports
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Structs
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    // Win32 Constants
    private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    public static bool PatchMemory(int processId, ulong address, byte[] patch, out string errorMessage)
    {
        errorMessage = string.Empty;
        IntPtr hProcess = IntPtr.Zero;

        try
        {
            hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                errorMessage = $"Failed to open process. Error code: {Marshal.GetLastWin32Error()}";
                return false;
            }

            IntPtr targetAddr = (IntPtr)address;
            uint oldProtect;

            // Change memory protection to read-write-execute
            if (!VirtualProtectEx(hProcess, targetAddr, (uint)patch.Length, 0x40, out oldProtect))
            {
                errorMessage = $"Failed to change memory protection. Error code: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // Write patch
            bool success = WriteProcessMemory(hProcess, targetAddr, patch, (uint)patch.Length, out IntPtr bytesWritten);
            
            // Restore protection
            VirtualProtectEx(hProcess, targetAddr, (uint)patch.Length, oldProtect, out _);

            if (!success || bytesWritten.ToInt32() != patch.Length)
            {
                errorMessage = $"Failed to write memory. Bytes written: {bytesWritten.ToInt32()} of {patch.Length}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Exception during memory patch: {ex.Message}";
            return false;
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    public static ulong ScanPattern(int processId, string moduleName, string pattern, out string errorMessage)
    {
        errorMessage = string.Empty;
        IntPtr hProcess = IntPtr.Zero;

        try
        {
            hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                errorMessage = $"Failed to open process. Error: {Marshal.GetLastWin32Error()}";
                return 0;
            }

            // Find module range
            IntPtr moduleBase = IntPtr.Zero;
            long moduleSize = 0;

            using (var process = Process.GetProcessById(processId))
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (moduleName.Equals(module.ModuleName, StringComparison.OrdinalIgnoreCase))
                    {
                        moduleBase = module.BaseAddress;
                        moduleSize = module.ModuleMemorySize;
                        break;
                    }
                }
            }

            if (moduleBase == IntPtr.Zero)
            {
                errorMessage = $"Module '{moduleName}' not found in target process.";
                return 0;
            }

            // Parse pattern
            var parsed = ParsePattern(pattern);
            if (parsed.Bytes.Length == 0)
            {
                errorMessage = "Invalid scan pattern format.";
                return 0;
            }

            // Read module memory
            byte[] moduleBytes = new byte[moduleSize];
            if (!ReadProcessMemory(hProcess, moduleBase, moduleBytes, (uint)moduleSize, out IntPtr bytesRead) || bytesRead.ToInt64() != moduleSize)
            {
                // Fallback: Scan memory pages
                return ScanPages(hProcess, moduleBase, moduleSize, parsed.Bytes, parsed.Mask);
            }

            // Scan local buffer
            int index = FindPatternIndex(moduleBytes, parsed.Bytes, parsed.Mask);
            if (index != -1)
            {
                return (ulong)moduleBase + (ulong)index;
            }

            errorMessage = "Pattern not found in module memory.";
            return 0;
        }
        catch (Exception ex)
        {
            errorMessage = $"Exception during pattern scan: {ex.Message}";
            return 0;
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private static ulong ScanPages(IntPtr hProcess, IntPtr startAddr, long size, byte[] pattern, bool[] mask)
    {
        IntPtr currentAddr = startAddr;
        IntPtr endAddr = startAddr + (int)size;
        int structSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (currentAddr.ToInt64() < endAddr.ToInt64())
        {
            int queryResult = VirtualQueryEx(hProcess, currentAddr, out MEMORY_BASIC_INFORMATION memInfo, (uint)structSize);
            if (queryResult == 0) break;

            if (memInfo.State == MEM_COMMIT && 
                (memInfo.Protect & PAGE_NOACCESS) == 0 && 
                (memInfo.Protect & PAGE_GUARD) == 0)
            {
                byte[] buffer = new byte[memInfo.RegionSize.ToInt32()];
                if (ReadProcessMemory(hProcess, memInfo.BaseAddress, buffer, (uint)buffer.Length, out IntPtr bytesRead))
                {
                    int index = FindPatternIndex(buffer, pattern, mask);
                    if (index != -1)
                    {
                        return (ulong)memInfo.BaseAddress + (ulong)index;
                    }
                }
            }

            currentAddr = memInfo.BaseAddress + memInfo.RegionSize.ToInt32();
        }

        return 0;
    }

    private static int FindPatternIndex(byte[] data, byte[] pattern, bool[] mask)
    {
        int patternLen = pattern.Length;
        int limit = data.Length - patternLen;

        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < patternLen; j++)
            {
                if (mask[j] && data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match) return i;
        }

        return -1;
    }

    private static (byte[] Bytes, bool[] Mask) ParsePattern(string pattern)
    {
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        var mask = new bool[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "?" || parts[i] == "??")
            {
                bytes[i] = 0;
                mask[i] = false;
            }
            else
            {
                bytes[i] = Convert.ToByte(parts[i], 16);
                mask[i] = true;
            }
        }

        return (bytes, mask);
    }
}
