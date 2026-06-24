using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OwnWand.App.Services;

public class ProcessService
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public Process? FindProcessByName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        return processes.FirstOrDefault();
    }

    public string? GetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // P/Invoke fallback for Access Denied (limited information query is allowed for elevated/different user games)
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    int size = 1024;
                    var builder = new StringBuilder(size);
                    if (QueryFullProcessImageName(hProcess, 0, builder, ref size))
                    {
                        return builder.ToString();
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
        }

        return null;
    }

    public bool IsProcessRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
