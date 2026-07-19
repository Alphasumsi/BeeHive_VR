using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BeeHiveVR.Services;

/// <summary>
/// Bindet alle von der WPF gestarteten Kindprozesse (Electron-Atlas,
/// capture-host, browser-host) über ein Windows <b>Job Object</b> mit dem Flag
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> an die Lebensdauer der WPF.
///
/// Stirbt die WPF — <b>egal wie</b> (sauberer Shutdown, <c>taskkill /F</c> /
/// <c>Stop-Process -Force</c>, AHK <c>WinKill</c>, Task-Manager „End Task",
/// Crash) — schließt das OS das einzige offene Job-Handle und der Kernel
/// terminiert alle zugewiesenen Prozesse sofort. Das ist die kugelsichere
/// Ergänzung zu <see cref="ElectronAtlasService.Stop"/> (läuft nur bei sauberem
/// <c>OnExit</c>) und zum je-Kind-Parent-Watch (pollt im Kind → verhungert wenn
/// der Atlas in einem Freeze hängt).
///
/// Die WPF wird selbst NIE dem Job zugewiesen — sie hält nur das Handle. Damit
/// trifft ein harter Kill der WPF nur sie, und der Job räumt die Kinder auf.
///
/// Lazy init: der Job entsteht bei der ersten <see cref="Assign"/> (also beim
/// ersten Kind-Spawn, iRacing-Connect). Bewusst KEIN Startup-Hook in
/// <c>App.OnStartup</c> — hält den Fußabdruck klein und die Datei eigenständig.
///
/// Das Handle wird absichtlich nie geschlossen: es lebt bis zum
/// Prozess-Ende, dann schließt es das OS → KILL_ON_JOB_CLOSE feuert.
/// </summary>
public static class ChildProcessJob
{
    private static readonly object _gate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _initTried;

    /// <summary>
    /// Weist einen frisch gestarteten Kindprozess dem Job zu. Idempotent-tolerant:
    /// legt den Job bei Bedarf an, schluckt Fehler (loggt Warnung) — schlägt die
    /// Zuweisung fehl, bleibt der Parent-Watch des Kindes der Fallback.
    /// </summary>
    public static void Assign(Process? process)
    {
        if (process == null) return;
        lock (_gate)
        {
            EnsureJob();
            if (_job == IntPtr.Zero) return; // kein Job → still weiter (Fallback greift)

            try
            {
                if (process.HasExited) return;
                if (AssignProcessToJobObject(_job, process.Handle))
                    Logger.Info($"ChildProcessJob: assigned pid={process.Id} (kill-on-close)");
                else
                    Logger.Warn($"ChildProcessJob: assign pid={process.Id} failed, " +
                                $"err={Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"ChildProcessJob.Assign: {ex.Message}");
            }
        }
    }

    // Muss unter _gate laufen.
    private static void EnsureJob()
    {
        if (_initTried) return;
        _initTried = true;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            Logger.Warn($"ChildProcessJob: CreateJobObject failed, err={Marshal.GetLastWin32Error()}");
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        int len = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
            {
                Logger.Warn($"ChildProcessJob: SetInformationJobObject failed, " +
                            $"err={Marshal.GetLastWin32Error()}");
                CloseHandle(job);
                return; // _job bleibt Zero → Fallback greift
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }

        _job = job;
        Logger.Info("ChildProcessJob: job created (KILL_ON_JOB_CLOSE) — Kinder sterben mit der WPF");
    }

    // ---- Win32 -----------------------------------------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
