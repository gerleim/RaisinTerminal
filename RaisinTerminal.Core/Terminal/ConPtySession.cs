using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// ConPTY wrapper — creates a pseudo-console and manages pipe I/O for a child process.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private IntPtr _hPC;
    private SafeFileHandle? _pipeIn;
    private SafeFileHandle? _pipeOut;
    private System.Diagnostics.Process? _process;
    private bool _disposed;

    public Stream? InputStream { get; private set; }
    public Stream? OutputStream { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler? Exited;

    public void Start(string command, int cols = 120, int rows = 30, string? workingDirectory = null)
    {
        CreatePipes(out var inputReadSide, out var inputWriteSide,
                    out var outputReadSide, out var outputWriteSide);

        _pipeIn = inputWriteSide;
        _pipeOut = outputReadSide;

        int hr = CreatePseudoConsole(
            new COORD { X = (short)cols, Y = (short)rows },
            inputReadSide.DangerousGetHandle(),
            outputWriteSide.DangerousGetHandle(),
            0, out _hPC);

        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        // Close the sides that the pseudo console now owns
        inputReadSide.Dispose();
        outputWriteSide.Dispose();

        _process = StartProcess(command, _hPC, workingDirectory);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);

        InputStream = new FileStream(_pipeIn, FileAccess.Write);
        OutputStream = new FileStream(_pipeOut, FileAccess.Read);
    }

    public void Resize(int cols, int rows)
    {
        if (_hPC != IntPtr.Zero)
            ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    }

    /// <summary>
    /// Reads the current working directory of the child process from its PEB.
    /// Returns null if the process has exited or the query fails.
    /// </summary>
    public string? GetWorkingDirectory()
    {
        if (_process == null || _process.HasExited) return null;
        try
        {
            var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, _process.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                // Step 1: Get PEB address
                var pbi = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
                if (status != 0) return null;

                // Step 2: Read ProcessParameters pointer from PEB (offset 0x20 on x64)
                IntPtr procParamsPtr;
                if (!ReadProcessMemory(handle, pbi.PebBaseAddress + 0x20, out procParamsPtr, (IntPtr)IntPtr.Size, out _))
                    return null;

                // Step 3: Read CurrentDirectory.Buffer (UNICODE_STRING at offset 0x38, Buffer pointer at 0x38+8=0x40 on x64)
                short dirLength;
                if (!ReadProcessMemory(handle, procParamsPtr + 0x38, out dirLength, (IntPtr)2, out _))
                    return null;

                IntPtr dirBufferPtr;
                if (!ReadProcessMemory(handle, procParamsPtr + 0x40, out dirBufferPtr, (IntPtr)IntPtr.Size, out _))
                    return null;

                if (dirLength <= 0 || dirLength > 1024) return null;

                var buffer = new byte[dirLength];
                if (!ReadProcessMemory(handle, dirBufferPtr, buffer, (IntPtr)buffer.Length, out _))
                    return null;

                var dir = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\\');
                return dir;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns true if the shell process (cmd.exe) has any child processes running
    /// (e.g. nslookup.exe, claude.exe, python.exe).
    /// </summary>
    public bool HasChildProcesses()
    {
        return GetChildProcessName() != null;
    }

    /// <summary>
    /// Returns the effective command name and PID of the first child process.
    /// For node.exe, inspects the command line to return the actual tool name (e.g. "claude").
    /// Returns (null, 0) if the shell has no children.
    /// </summary>
    public (string? Name, int Pid) GetChildProcessInfo()
    {
        if (_process == null || _process.HasExited) return (null, 0);
        try
        {
            int parentId = _process.Id;
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE) return (null, 0);
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snapshot, ref entry)) return (null, 0);
                do
                {
                    if (entry.th32ParentProcessID == parentId)
                    {
                        var name = entry.szExeFile;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = name[..^4];

                        // node.exe is often a wrapper — check command line for the actual tool
                        if (name.Equals("node", StringComparison.OrdinalIgnoreCase))
                        {
                            var cmdLine = GetProcessCommandLine(entry.th32ProcessID);
                            if (cmdLine != null)
                            {
                                // Look for known CLI tools in the command line path
                                if (cmdLine.Contains("claude-code", StringComparison.OrdinalIgnoreCase) ||
                                    cmdLine.Contains("@anthropic-ai", StringComparison.OrdinalIgnoreCase))
                                    return ("claude", entry.th32ProcessID);
                            }
                        }

                        return (name, entry.th32ProcessID);
                    }
                } while (Process32Next(snapshot, ref entry));
                return (null, 0);
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        catch { return (null, 0); }
    }

    /// <summary>
    /// Returns the effective command name of the first child process.
    /// </summary>
    public string? GetChildProcessName() => GetChildProcessInfo().Name;

    /// <summary>
    /// Reads the command line of a process from its PEB.
    /// </summary>
    private static string? GetProcessCommandLine(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0)
                return null;

            IntPtr procParamsPtr;
            if (!ReadProcessMemory(handle, pbi.PebBaseAddress + 0x20, out procParamsPtr, (IntPtr)IntPtr.Size, out _))
                return null;

            // CommandLine UNICODE_STRING is at offset 0x70 in RTL_USER_PROCESS_PARAMETERS (x64)
            short cmdLength;
            if (!ReadProcessMemory(handle, procParamsPtr + 0x70, out cmdLength, (IntPtr)2, out _))
                return null;

            IntPtr cmdBufferPtr;
            if (!ReadProcessMemory(handle, procParamsPtr + 0x78, out cmdBufferPtr, (IntPtr)IntPtr.Size, out _))
                return null;

            if (cmdLength <= 0 || cmdLength > 32766) return null;

            var buffer = new byte[cmdLength];
            if (!ReadProcessMemory(handle, cmdBufferPtr, buffer, (IntPtr)buffer.Length, out _))
                return null;

            return System.Text.Encoding.Unicode.GetString(buffer);
        }
        catch { return null; }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _process?.Kill(); } catch { }
        _process?.Dispose();
        InputStream?.Dispose();
        OutputStream?.Dispose();
        _pipeIn?.Dispose();
        _pipeOut?.Dispose();

        if (_hPC != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }
    }

    private static void CreatePipes(
        out SafeFileHandle inputReadSide, out SafeFileHandle inputWriteSide,
        out SafeFileHandle outputReadSide, out SafeFileHandle outputWriteSide)
    {
        if (!CreatePipe(out inputReadSide, out inputWriteSide, IntPtr.Zero, 0))
            throw new InvalidOperationException("Failed to create input pipe");
        if (!CreatePipe(out outputReadSide, out outputWriteSide, IntPtr.Zero, 0))
            throw new InvalidOperationException("Failed to create output pipe");
    }

    private static System.Diagnostics.Process StartProcess(string command, IntPtr hPC, string? workingDirectory = null)
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        IntPtr lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);

        if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");

        if (!UpdateProcThreadAttribute(startupInfo.lpAttributeList, 0,
            (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC,
            (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");

        if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref startupInfo,
            out var processInfo))
            throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

        var process = System.Diagnostics.Process.GetProcessById(processInfo.dwProcessId);

        CloseHandle(processInfo.hProcess);
        CloseHandle(processInfo.hThread);
        DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
        Marshal.FreeHGlobal(startupInfo.lpAttributeList);

        return process;
    }

    // P/Invoke declarations
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // For reading child process working directory
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        out IntPtr lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        out short lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    // Toolhelp32 — for enumerating child processes
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
}
