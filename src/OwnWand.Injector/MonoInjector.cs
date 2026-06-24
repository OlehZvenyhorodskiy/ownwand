using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OwnWand.Injector;

public static class MonoInjector
{
    // Win32 API Imports
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibraryA(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hLibModule);

    // Win32 Constants
    private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint MEM_RELEASE = 0x8000;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint INFINITE = 0xFFFFFFFF;

    public static bool Inject(int processId, string dllPath, string @namespace, string className, string methodName, out string errorMessage)
    {
        errorMessage = string.Empty;
        IntPtr hProcess = IntPtr.Zero;
        IntPtr localMono = IntPtr.Zero;

        try
        {
            // 1. Open target process
            hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                errorMessage = $"Failed to open process. Error code: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // 2. Find Mono module in target process and identify the correct DLL name
            var (targetMonoBase, monoModuleName) = FindMonoModuleInTarget(processId);
            if (targetMonoBase == IntPtr.Zero || string.IsNullOrEmpty(monoModuleName))
            {
                errorMessage = "Failed to find Mono module (mono.dll or mono-2.0-bdwgc.dll) in target process.";
                return false;
            }

            // 3. Find the full path to the Mono DLL in the target game's folders to load the exact same module locally
            string localMonoPath = FindLocalMonoDllPath(processId, monoModuleName);
            if (string.IsNullOrEmpty(localMonoPath) || !File.Exists(localMonoPath))
            {
                errorMessage = $"Failed to locate mono DLL locally at: {localMonoPath}";
                return false;
            }

            // 4. Load Mono DLL locally to calculate function offsets
            localMono = LoadLibraryA(localMonoPath);
            if (localMono == IntPtr.Zero)
            {
                errorMessage = $"Failed to load mono module locally from: {localMonoPath}";
                return false;
            }

            // 5. Resolve offsets of Mono functions
            var monoGetRootDomain = GetTargetAddress(localMono, targetMonoBase, "mono_get_root_domain");
            var monoThreadAttach = GetTargetAddress(localMono, targetMonoBase, "mono_thread_attach");
            var monoDomainAssemblyOpen = GetTargetAddress(localMono, targetMonoBase, "mono_domain_assembly_open");
            var monoAssemblyGetImage = GetTargetAddress(localMono, targetMonoBase, "mono_assembly_get_image");
            var monoClassFromName = GetTargetAddress(localMono, targetMonoBase, "mono_class_from_name");
            var monoClassGetMethodFromName = GetTargetAddress(localMono, targetMonoBase, "mono_class_get_method_from_name");
            var monoRuntimeInvoke = GetTargetAddress(localMono, targetMonoBase, "mono_runtime_invoke");

            if (monoGetRootDomain == 0 || monoThreadAttach == 0 || monoDomainAssemblyOpen == 0 ||
                monoAssemblyGetImage == 0 || monoClassFromName == 0 || monoClassGetMethodFromName == 0 ||
                monoRuntimeInvoke == 0)
            {
                errorMessage = "Failed to resolve one or more Mono function exports.";
                return false;
            }

            // 6. Allocate memory in target process for strings
            var dllPathBytes = Encoding.UTF8.GetBytes(dllPath + "\0");
            var namespaceBytes = Encoding.UTF8.GetBytes(@namespace + "\0");
            var classBytes = Encoding.UTF8.GetBytes(className + "\0");
            var methodBytes = Encoding.UTF8.GetBytes(methodName + "\0");

            IntPtr dllPathAddr = AllocateAndWrite(hProcess, dllPathBytes);
            IntPtr namespaceAddr = AllocateAndWrite(hProcess, namespaceBytes);
            IntPtr classAddr = AllocateAndWrite(hProcess, classBytes);
            IntPtr methodAddr = AllocateAndWrite(hProcess, methodBytes);

            if (dllPathAddr == IntPtr.Zero || namespaceAddr == IntPtr.Zero || classAddr == IntPtr.Zero || methodAddr == IntPtr.Zero)
            {
                errorMessage = "Failed to allocate memory for string arguments in target process.";
                return false;
            }

            // 7. Generate x64 shellcode
            var shellcode = BuildShellcode(
                (ulong)monoGetRootDomain,
                (ulong)monoThreadAttach,
                (ulong)monoDomainAssemblyOpen,
                (ulong)monoAssemblyGetImage,
                (ulong)monoClassFromName,
                (ulong)monoClassGetMethodFromName,
                (ulong)monoRuntimeInvoke,
                (ulong)dllPathAddr,
                (ulong)namespaceAddr,
                (ulong)classAddr,
                (ulong)methodAddr
            );

            // 8. Write shellcode to executable memory in target process
            IntPtr shellcodeAddr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)shellcode.Length, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (shellcodeAddr == IntPtr.Zero)
            {
                errorMessage = $"Failed to allocate memory for shellcode in target. Error: {Marshal.GetLastWin32Error()}";
                return false;
            }

            if (!WriteProcessMemory(hProcess, shellcodeAddr, shellcode, (uint)shellcode.Length, out _))
            {
                errorMessage = $"Failed to write shellcode to target process. Error: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // 9. Execute shellcode via CreateRemoteThread
            IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, shellcodeAddr, IntPtr.Zero, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                errorMessage = $"Failed to create remote thread in target. Error: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // 10. Wait for thread to finish
            uint waitResult = WaitForSingleObject(hThread, 10000); // 10 second timeout
            CloseHandle(hThread);

            if (waitResult == WAIT_FAILED)
            {
                errorMessage = $"Wait failed on remote thread execution. Error: {Marshal.GetLastWin32Error()}";
                return false;
            }

            // Clean up allocated memory in target process
            VirtualFreeEx(hProcess, shellcodeAddr, 0, MEM_RELEASE);
            VirtualFreeEx(hProcess, dllPathAddr, 0, MEM_RELEASE);
            VirtualFreeEx(hProcess, namespaceAddr, 0, MEM_RELEASE);
            VirtualFreeEx(hProcess, classAddr, 0, MEM_RELEASE);
            VirtualFreeEx(hProcess, methodAddr, 0, MEM_RELEASE);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Exception occurred during injection: {ex.Message}";
            return false;
        }
        finally
        {
            if (localMono != IntPtr.Zero) FreeLibrary(localMono);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    private static (IntPtr BaseAddress, string ModuleName) FindMonoModuleInTarget(int processId)
    {
        using var process = Process.GetProcessById(processId);
        foreach (ProcessModule module in process.Modules)
        {
            string name = module.ModuleName ?? string.Empty;
            if (name.Equals("mono.dll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase))
            {
                return (module.BaseAddress, name);
            }
        }
        return (IntPtr.Zero, string.Empty);
    }

    private static string FindLocalMonoDllPath(int processId, string monoModuleName)
    {
        using var process = Process.GetProcessById(processId);
        foreach (ProcessModule module in process.Modules)
        {
            if (module.ModuleName != null && module.ModuleName.Equals(monoModuleName, StringComparison.OrdinalIgnoreCase))
            {
                return module.FileName ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static ulong GetTargetAddress(IntPtr localBase, IntPtr targetBase, string functionName)
    {
        IntPtr localFuncAddr = GetProcAddress(localBase, functionName);
        if (localFuncAddr == IntPtr.Zero) return 0;

        ulong offset = (ulong)localFuncAddr - (ulong)localBase;
        return (ulong)targetBase + offset;
    }

    private static IntPtr AllocateAndWrite(IntPtr hProcess, byte[] buffer)
    {
        IntPtr addr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)buffer.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (addr == IntPtr.Zero) return IntPtr.Zero;

        if (WriteProcessMemory(hProcess, addr, buffer, (uint)buffer.Length, out _))
        {
            return addr;
        }

        VirtualFreeEx(hProcess, addr, 0, MEM_RELEASE);
        return IntPtr.Zero;
    }

    private static byte[] BuildShellcode(
        ulong mono_get_root_domain_addr,
        ulong mono_thread_attach_addr,
        ulong mono_domain_assembly_open_addr,
        ulong mono_assembly_get_image_addr,
        ulong mono_class_from_name_addr,
        ulong mono_class_get_method_from_name_addr,
        ulong mono_runtime_invoke_addr,
        ulong dllPath_addr,
        ulong namespace_addr,
        ulong class_addr,
        ulong method_addr)
    {
        var code = new List<byte>();

        // sub rsp, 40 (shadow space + stack alignment)
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });

        // --- Call mono_get_root_domain ---
        // mov rax, mono_get_root_domain_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_get_root_domain_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        // mov r12, rax (save domain in r12)
        code.AddRange(new byte[] { 0x49, 0x89, 0xC4 });

        // --- Call mono_thread_attach(domain) ---
        // mov rcx, r12
        code.AddRange(new byte[] { 0x4C, 0x89, 0xE1 });
        // mov rax, mono_thread_attach_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_thread_attach_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });

        // --- Call mono_domain_assembly_open(domain, dllPath) ---
        // mov rcx, r12
        code.AddRange(new byte[] { 0x4C, 0x89, 0xE1 });
        // mov rdx, dllPath_addr
        code.AddRange(new byte[] { 0x48, 0xBA });
        code.AddRange(BitConverter.GetBytes(dllPath_addr));
        // mov rax, mono_domain_assembly_open_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_domain_assembly_open_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        // mov r13, rax (save assembly in r13)
        code.AddRange(new byte[] { 0x49, 0x89, 0xC5 });

        // --- Call mono_assembly_get_image(assembly) ---
        // mov rcx, r13
        code.AddRange(new byte[] { 0x4C, 0x89, 0xE9 });
        // mov rax, mono_assembly_get_image_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_assembly_get_image_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        // mov r14, rax (save image in r14)
        code.AddRange(new byte[] { 0x49, 0x89, 0xC6 });

        // --- Call mono_class_from_name(image, namespace, className) ---
        // mov rcx, r14
        code.AddRange(new byte[] { 0x4C, 0x89, 0xF1 });
        // mov rdx, namespace_addr
        code.AddRange(new byte[] { 0x48, 0xBA });
        code.AddRange(BitConverter.GetBytes(namespace_addr));
        // mov r8, class_addr
        code.AddRange(new byte[] { 0x49, 0xB8 });
        code.AddRange(BitConverter.GetBytes(class_addr));
        // mov rax, mono_class_from_name_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_class_from_name_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        // mov r15, rax (save class in r15)
        code.AddRange(new byte[] { 0x49, 0x89, 0xC7 });

        // --- Call mono_class_get_method_from_name(klass, methodName, 0) ---
        // mov rcx, r15
        code.AddRange(new byte[] { 0x4C, 0x89, 0xF9 });
        // mov rdx, method_addr
        code.AddRange(new byte[] { 0x48, 0xBA });
        code.AddRange(BitConverter.GetBytes(method_addr));
        // xor r8d, r8d
        code.AddRange(new byte[] { 0x45, 0x31, 0xC0 });
        // mov rax, mono_class_get_method_from_name_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_class_get_method_from_name_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });

        // --- Call mono_runtime_invoke(method, NULL, NULL, NULL) ---
        // mov rcx, rax (method)
        code.AddRange(new byte[] { 0x48, 0x89, 0xC1 });
        // xor edx, edx
        code.AddRange(new byte[] { 0x31, 0xD2 });
        // xor r8d, r8d
        code.AddRange(new byte[] { 0x45, 0x31, 0xC0 });
        // xor r9d, r9d
        code.AddRange(new byte[] { 0x45, 0x31, 0xC9 });
        // mov rax, mono_runtime_invoke_addr
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(mono_runtime_invoke_addr));
        // call rax
        code.AddRange(new byte[] { 0xFF, 0xD0 });

        // add rsp, 40
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });

        // ret
        code.Add(0xC3);

        return code.ToArray();
    }
}
