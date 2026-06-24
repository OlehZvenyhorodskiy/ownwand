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

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpAddress, uint dwSize);

    // Token Privilege Imports
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private const string SE_DEBUG_NAME = "SeDebugPrivilege";
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public static bool EnableDebugPrivilege()
    {
        IntPtr hToken;
        IntPtr hCurrentProcess = Process.GetCurrentProcess().Handle;

        if (!OpenProcessToken(hCurrentProcess, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
            return false;

        try
        {
            LUID luid;
            if (!LookupPrivilegeValue(null!, SE_DEBUG_NAME, out luid))
                return false;

            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(hToken);
        }
    }

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

    public static ulong GetGWorldAddress(int processId, string moduleName)
    {
        string pattern = "48 8B 1D ? ? ? ? 48 85 DB 74 3B";
        ulong sigAddress = ScanPattern(processId, moduleName, pattern, out _);
        if (sigAddress == 0) return 0;

        byte[] offsetBytes = ReadMemoryBytes(processId, sigAddress + 3, 4);
        if (offsetBytes.Length < 4) return 0;

        int offset = BitConverter.ToInt32(offsetBytes, 0);
        return (ulong)((long)sigAddress + offset + 7);
    }

    public static byte[] ReadMemoryBytes(int processId, ulong address, uint size)
    {
        byte[] buffer = new byte[size];
        IntPtr hProcess = OpenProcess(0x0010 /* PROCESS_VM_READ */, false, processId);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                ReadProcessMemory(hProcess, (IntPtr)address, buffer, size, out _);
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        return buffer;
    }

    public static ulong ReadPointer(int processId, ulong address)
    {
        byte[] buffer = ReadMemoryBytes(processId, address, 8);
        if (buffer.Length < 8) return 0;
        return (ulong)BitConverter.ToInt64(buffer, 0);
    }

    public static bool PatchMemory(int processId, ulong address, byte[] patch, out string errorMessage)
    {
        errorMessage = string.Empty;
        IntPtr hProcess = IntPtr.Zero;

        // Elevate security context
        EnableDebugPrivilege();

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
            
            // Flush instruction cache for CPU core synchronization
            FlushInstructionCache(hProcess, targetAddr, (uint)patch.Length);

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
            EnableDebugPrivilege();
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
