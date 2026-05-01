using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// ConPTY wrapper — creates a pseudo-console and manages pipe I/O for a child process.
/// </summary>
public sealed partial class ConPtySession : IDisposable
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
                var procParamsPtr = ReadProcParamsPtr(handle);
                if (procParamsPtr == IntPtr.Zero) return null;

                // CurrentDirectory UNICODE_STRING at offset 0x38 in RTL_USER_PROCESS_PARAMETERS (x64)
                return ReadPebUnicodeString(handle, procParamsPtr, 0x38, 1024)?.TrimEnd('\\');
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
            var procParamsPtr = ReadProcParamsPtr(handle);
            if (procParamsPtr == IntPtr.Zero) return null;

            // CommandLine UNICODE_STRING at offset 0x70 in RTL_USER_PROCESS_PARAMETERS (x64)
            return ReadPebUnicodeString(handle, procParamsPtr, 0x70, 32766);
        }
        catch { return null; }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Reads the ProcessParameters pointer from a process's PEB.
    /// Returns IntPtr.Zero on failure.
    /// </summary>
    private static IntPtr ReadProcParamsPtr(IntPtr processHandle)
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        if (NtQueryInformationProcess(processHandle, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0)
            return IntPtr.Zero;

        if (!ReadProcessMemory(processHandle, pbi.PebBaseAddress + 0x20, out IntPtr procParamsPtr, (IntPtr)IntPtr.Size, out _))
            return IntPtr.Zero;

        return procParamsPtr;
    }

    /// <summary>
    /// Reads a UNICODE_STRING from RTL_USER_PROCESS_PARAMETERS at the given offset.
    /// The UNICODE_STRING has Length (ushort) at offset, Buffer pointer at offset+8 (x64).
    /// </summary>
    private static string? ReadPebUnicodeString(IntPtr processHandle, IntPtr procParamsPtr, int offset, int maxLength)
    {
        if (!ReadProcessMemory(processHandle, procParamsPtr + offset, out short length, (IntPtr)2, out _))
            return null;

        if (!ReadProcessMemory(processHandle, procParamsPtr + offset + 8, out IntPtr bufferPtr, (IntPtr)IntPtr.Size, out _))
            return null;

        if (length <= 0 || length > maxLength) return null;

        var buffer = new byte[length];
        if (!ReadProcessMemory(processHandle, bufferPtr, buffer, (IntPtr)buffer.Length, out _))
            return null;

        return System.Text.Encoding.Unicode.GetString(buffer);
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
}
