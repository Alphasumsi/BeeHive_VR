// PERF-WORK DEAKTIVIERT (Chat 30.5.2026) — kompletter Inhalt eingerahmt,
// damit nichts kompiliert wird. Reaktivieren: #if false / #endif entfernen.
#if false
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BeeHiveVR.Services;

/// <summary>
/// Pollt periodisch CPU% + Working-Set für die von BrowserHostManager gestarteten
/// browser-host.exe-Prozesse PLUS deren WebView2-Subprozesse (Renderer/GPU/Network).
///
/// Läuft im WPF-Prozess — Toolhelp32-Enumeration ist hier sicher (im Gegensatz zum
/// Layer in iRacings Adressraum, wo das crasht — siehe project_no_process_enum_in_layer).
/// </summary>
public sealed class HostStatsPoller
{
    private static HostStatsPoller? _instance;
    public static HostStatsPoller Instance => _instance ??= new HostStatsPoller();

    public sealed class HostStats
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";   // Anzeige-Name aus SourceModel.Name
        public int RootPid { get; init; }
        public int ProcCount { get; init; }
        public float CpuPct { get; init; }        // % einer Logical-Core; Summe über alle Subprozesse
        public float MemMB { get; init; }
    }

    /// <summary>Letzter Stand pro Source-Id. Atomar ersetzt — Reader kann ohne Lock lesen.</summary>
    public IReadOnlyDictionary<string, HostStats> Current { get; private set; } =
        new Dictionary<string, HostStats>();

    public event EventHandler<IReadOnlyDictionary<string, HostStats>>? Updated;

    private CancellationTokenSource? _cts;
    private Thread? _thread;

    // Per-Id state für CPU-Delta-Berechnung.
    private sealed class State
    {
        public long LastUserKernel100ns;
        public DateTime LastSampleUtc;
    }
    private readonly Dictionary<string, State> _state = new();

    private HostStatsPoller() { }

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token))
        {
            IsBackground = true,
            Name = "HostStatsPoller",
        };
        _thread.Start();
        Logger.Info("HostStatsPoller: started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _thread?.Join(2000); } catch { }
        _thread = null;
        _cts = null;
    }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Logger.Warn($"HostStatsPoller: tick failed: {ex.Message}");
            }
            // Genau eine Toolhelp32-Enumeration pro 500ms — sparsam.
            if (ct.WaitHandle.WaitOne(500)) break;
        }
    }

    private void Tick()
    {
        // Roots holen — Snapshot vom BrowserHostManager, damit wir nicht im Lock ackern.
        var roots = BrowserHostManager.Instance.EnumerateActive();
        if (roots.Count == 0)
        {
            if (Current.Count > 0)
            {
                Current = new Dictionary<string, HostStats>();
                Updated?.Invoke(this, Current);
            }
            return;
        }

        // Parent→Children-Map einmal pro Tick aufbauen (Toolhelp32-Snapshot ist nicht billig).
        var parentToChildren = BuildParentMap();

        var now = DateTime.UtcNow;
        var result = new Dictionary<string, HostStats>(roots.Count);

        foreach (var (id, rootPid, name) in roots)
        {
            // Descendants via BFS.
            var pids = new List<int> { rootPid };
            for (int i = 0; i < pids.Count; i++)
            {
                if (parentToChildren.TryGetValue(pids[i], out var kids))
                {
                    foreach (var k in kids)
                    {
                        if (!pids.Contains(k)) pids.Add(k);
                    }
                }
            }

            long totalUK = 0;
            long totalMem = 0;
            int alive = 0;
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    totalUK += (p.UserProcessorTime + p.PrivilegedProcessorTime).Ticks;
                    totalMem += p.WorkingSet64;
                    alive++;
                }
                catch { /* exited zwischen Snapshot und GetProcessById */ }
            }

            float cpuPct = 0f;
            if (_state.TryGetValue(id, out var st))
            {
                var dtMs = (now - st.LastSampleUtc).TotalMilliseconds;
                var dUK100ns = totalUK - st.LastUserKernel100ns;
                if (dtMs > 0 && dUK100ns > 0)
                {
                    // 100ns-Ticks → ms: /10000. %-of-one-core = cpuMs / wallMs * 100.
                    var cpuMs = dUK100ns / 10000.0;
                    cpuPct = (float)(cpuMs / dtMs * 100.0);
                }
                st.LastUserKernel100ns = totalUK;
                st.LastSampleUtc = now;
            }
            else
            {
                _state[id] = new State
                {
                    LastUserKernel100ns = totalUK,
                    LastSampleUtc = now,
                };
            }

            result[id] = new HostStats
            {
                Id = id,
                Name = name,
                RootPid = rootPid,
                ProcCount = alive,
                CpuPct = cpuPct,
                MemMB = totalMem / (1024f * 1024f),
            };
        }

        // Tote Ids aus dem State räumen.
        foreach (var deadId in _state.Keys.Where(k => !result.ContainsKey(k)).ToList())
        {
            _state.Remove(deadId);
        }

        Current = result;
        Updated?.Invoke(this, result);
    }

    // --- Toolhelp32 P/Invoke ----------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private static Dictionary<int, List<int>> BuildParentMap()
    {
        var map = new Dictionary<int, List<int>>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref pe))
            {
                do
                {
                    int parent = (int)pe.th32ParentProcessID;
                    int child = (int)pe.th32ProcessID;
                    if (!map.TryGetValue(parent, out var list))
                    {
                        list = new List<int>();
                        map[parent] = list;
                    }
                    list.Add(child);
                    pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
                } while (Process32NextW(snap, ref pe));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }
}
#endif
