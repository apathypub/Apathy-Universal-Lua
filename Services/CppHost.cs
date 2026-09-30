using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Apathy.Services;

/// <summary>
/// Runs .cpp scripts by compiling them to a DLL (using MSVC cl.exe or GCC g++) and calling
/// three exported functions: <c>apathy_init</c>, <c>apathy_tick</c>, <c>apathy_unload</c>.
/// An <c>apathy.h</c> header with the C API is generated next to the script before compilation.
/// Permissions: <c>// @permissions input</c>.
/// </summary>
public sealed class CppHost : IScriptHost
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InitDelegate(IntPtr api);

    // ── Native function-pointer table passed to the script DLL ───────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LogFn([MarshalAs(UnmanagedType.LPStr)] string msg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendKeyFn(int vk, int down);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MouseButtonFn(int btn, int down);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MouseScrollFn(int delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NotifyFn([MarshalAs(UnmanagedType.LPStr)] string msg);

    // Keep delegate references alive so the GC doesn't collect them while the DLL holds function pointers.
    private LogFn?         _logFn;
    private SendKeyFn?     _sendKeyFn;
    private MouseButtonFn? _mouseButtonFn;
    private MouseScrollFn? _mouseScrollFn;
    private NotifyFn?      _notifyFn;

    [StructLayout(LayoutKind.Sequential)]
    private struct ApathyApi
    {
        public IntPtr log;
        public IntPtr send_key;
        public IntPtr mouse_button;
        public IntPtr mouse_scroll;
        public IntPtr notify;
    }

    // ── Per-script state ─────────────────────────────────────────────────────
    private sealed class CppInstance(string name, string dllPath, IntPtr hModule,
                                     VoidDelegate tick, VoidDelegate unload)
    {
        public readonly string Name = name;
        public readonly string DllPath = dllPath;
        public IntPtr HModule = hModule;
        public readonly VoidDelegate Tick = tick;
        public readonly VoidDelegate Unload = unload;
        public IntPtr ApiPtr = IntPtr.Zero;
    }

    private readonly Dictionary<string, CppInstance> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    public CppHost()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public bool IsRunning(string name) => _running.ContainsKey(name);
    public IEnumerable<string> RunningNames => _running.Keys;

    public bool Run(string name, string path, bool reload = false)
    {
        if (_running.ContainsKey(name) && !reload) return true;
        Stop(name);

        string source;
        try { source = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LuaLog.Write($"[{name}] cannot read file: {ex.Message}");
            NotificationService.Show($"{name} failed to load", ex.Message, NotificationKind.Error);
            return false;
        }

        var requested = ScriptPermissions.Parse(source, w => LuaLog.Write($"[{name}] {w}"));
        if (!ScriptPermissions.Approve(name, requested)) return false;

        // Find a C++ compiler.
        string? compiler = FindCompiler(out bool isMsvc);
        if (compiler == null)
        {
            const string msg = "No C++ compiler found. Install Visual Studio (with C++ workload) or MinGW-w64 and add g++ to PATH.";
            LuaLog.Write($"[{name}] {msg}");
            NotificationService.Show($"{name} failed to load", msg, NotificationKind.Error);
            return false;
        }

        // Compile.
        string buildDir = BuildDir(name);
        try
        {
            Directory.CreateDirectory(buildDir);
            WriteHeader(buildDir, requested);
            string dllPath = Compile(compiler, isMsvc, path, buildDir, name);
            if (dllPath == null!) return false; // errors already logged

            // Load the DLL.
            var hModule = LoadLibraryW(dllPath);
            if (hModule == IntPtr.Zero)
            {
                string err = $"LoadLibrary failed (error {Marshal.GetLastWin32Error()})";
                LuaLog.Write($"[{name}] {err}");
                NotificationService.Show($"{name} failed to load", err, NotificationKind.Error);
                return false;
            }

            // Resolve exports.
            var initPtr   = GetProcAddress(hModule, "apathy_init");
            var tickPtr   = GetProcAddress(hModule, "apathy_tick");
            var unloadPtr = GetProcAddress(hModule, "apathy_unload");

            if (initPtr == IntPtr.Zero || tickPtr == IntPtr.Zero || unloadPtr == IntPtr.Zero)
            {
                FreeLibrary(hModule);
                string err = "DLL must export apathy_init, apathy_tick and apathy_unload.";
                LuaLog.Write($"[{name}] {err}");
                NotificationService.Show($"{name} failed", err, NotificationKind.Error);
                return false;
            }

            var initFn   = Marshal.GetDelegateForFunctionPointer<InitDelegate>(initPtr);
            var tickFn   = Marshal.GetDelegateForFunctionPointer<VoidDelegate>(tickPtr);
            var unloadFn = Marshal.GetDelegateForFunctionPointer<VoidDelegate>(unloadPtr);

            // Build and pin the API struct.
            var scriptName = name;
            _logFn         = msg => LuaLog.Write($"[{scriptName}] {msg}");
            _sendKeyFn     = (vk, down) => Native.SendKey(vk, down != 0);
            _mouseButtonFn = (btn, down) => Native.SendMouseButton(btn, down != 0);
            _mouseScrollFn = delta => { if (delta != 0) Native.ScrollMouse(delta); };
            _notifyFn      = msg => NotificationService.Show(scriptName, msg);

            var api = new ApathyApi
            {
                log            = Marshal.GetFunctionPointerForDelegate(_logFn),
                send_key       = Marshal.GetFunctionPointerForDelegate(_sendKeyFn),
                mouse_button   = Marshal.GetFunctionPointerForDelegate(_mouseButtonFn),
                mouse_scroll   = Marshal.GetFunctionPointerForDelegate(_mouseScrollFn),
                notify         = Marshal.GetFunctionPointerForDelegate(_notifyFn),
            };

            // Pass the API struct to the DLL (caller keeps it alive as a local field — but
            // the DLL receives a pointer to stack memory which will be freed; allocate on heap).
            var apiPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ApathyApi>());
            Marshal.StructureToPtr(api, apiPtr, false);

            try { initFn(apiPtr); }
            catch (Exception ex)
            {
                Marshal.FreeHGlobal(apiPtr);
                FreeLibrary(hModule);
                LuaLog.Write($"[{name}] apathy_init threw: {ex.Message}");
                NotificationService.Show($"{name} failed", ex.Message, NotificationKind.Error);
                return false;
            }

            // Store api pointer in instance; freed in Stop().
            var inst = new CppInstance(name, dllPath, hModule, tickFn, unloadFn);
            inst.ApiPtr = apiPtr;
            _running[name] = inst;
            LuaLog.Write($"[{name}] loaded (C++)");
            return true;
        }
        catch (Exception ex)
        {
            LuaLog.Write($"[{name}] error: {ex.Message}");
            NotificationService.Show($"{name} failed", ex.Message, NotificationKind.Error);
            return false;
        }
    }

    public void Stop(string name)
    {
        if (!_running.Remove(name, out var inst)) return;
        try { inst.Unload(); } catch (Exception ex) { LuaLog.Write($"[{name}] unload: {ex.Message}"); }
        if (inst.ApiPtr != IntPtr.Zero) Marshal.FreeHGlobal(inst.ApiPtr);
        FreeLibrary(inst.HModule);
        LuaLog.Write($"[{name}] unloaded (C++)");
    }

    public void StopAll()
    {
        foreach (var name in _running.Keys.ToArray()) Stop(name);
    }

    private void Tick()
    {
        foreach (var inst in _running.Values.ToArray())
        {
            try { inst.Tick(); }
            catch (Exception ex)
            {
                LuaLog.Write($"[{inst.Name}] runtime error: {ex.Message}");
                NotificationService.Show($"{inst.Name} stopped", ex.Message, NotificationKind.Error);
                _running.Remove(inst.Name);
                try { inst.Unload(); } catch { }
                if (inst.ApiPtr != IntPtr.Zero) Marshal.FreeHGlobal(inst.ApiPtr);
                FreeLibrary(inst.HModule);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildDir(string name) =>
        Path.Combine(Path.GetTempPath(), "apathy_cpp", name);

    private static string? FindCompiler(out bool isMsvc)
    {
        // 1. Try cl.exe via vswhere
        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(vswhere,
                    "-latest -products * -requires Microsoft.VisualCpp.Tools.HostX64.TargetX64 -find VC\\Tools\\MSVC\\**\\bin\\Hostx64\\x64\\cl.exe")
                { RedirectStandardOutput = true, UseShellExecute = false });
                string? line = p?.StandardOutput.ReadLine()?.Trim();
                p?.WaitForExit(3000);
                if (!string.IsNullOrEmpty(line) && File.Exists(line))
                {
                    isMsvc = true;
                    return line;
                }
            }
            catch { }
        }

        // 2. Search VS 2022/2019 paths directly
        foreach (string vsRoot in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio"),
        })
        {
            if (!Directory.Exists(vsRoot)) continue;
            foreach (string cl in Directory.EnumerateFiles(vsRoot, "cl.exe", SearchOption.AllDirectories))
            {
                if (cl.Contains("Hostx64") && cl.Contains("x64"))
                {
                    isMsvc = true;
                    return cl;
                }
            }
        }

        // 3. Try g++ in PATH
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string gpp = Path.Combine(dir.Trim(), "g++.exe");
            if (File.Exists(gpp)) { isMsvc = false; return gpp; }
        }

        isMsvc = false;
        return null;
    }

    private string Compile(string compiler, bool isMsvc, string sourcePath, string buildDir, string name)
    {
        string dllPath = Path.Combine(buildDir, name + ".dll");

        // Copy (don't compile in-place in case the path has spaces/non-ASCII).
        string tempSrc = Path.Combine(buildDir, name + ".cpp");
        File.Copy(sourcePath, tempSrc, overwrite: true);

        string args = isMsvc
            ? $"/nologo /O2 /EHsc /std:c++17 /LD \"{tempSrc}\" /Fe:\"{dllPath}\" /I\"{buildDir}\""
            : $"-O2 -std=c++17 -shared -fPIC -I\"{buildDir}\" \"{tempSrc}\" -o \"{dllPath}\"";

        using var p = Process.Start(new ProcessStartInfo(compiler, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = buildDir,
        }) ?? throw new InvalidOperationException("Failed to start compiler.");

        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);

        if (p.ExitCode != 0)
        {
            LuaLog.Write($"[{name}] compile errors:");
            foreach (var line in output.Split('\n').Take(20))
                if (!string.IsNullOrWhiteSpace(line)) LuaLog.Write($"  {line.TrimEnd()}");
            NotificationService.Show($"{name} compile failed", $"Exit {p.ExitCode} — see the log.", NotificationKind.Error);
            return null!;
        }

        return dllPath;
    }

    private static void WriteHeader(string dir, IReadOnlySet<string> permissions)
    {
        string path = Path.Combine(dir, "apathy.h");
        if (File.Exists(path)) return; // no need to regenerate each time

        File.WriteAllText(path,
            """
            #pragma once
            #include <stdint.h>

            /* Function-pointer types for the apathy API. */
            typedef void (*ApathyLogFn)   (const char* msg);
            typedef void (*ApathySendKeyFn)(int vk, int down);      /* down: 1=press, 0=release */
            typedef void (*ApathyMouseButtonFn)(int btn, int down);  /* btn: 1=L 2=R 3=M 4=X1 5=X2 */
            typedef void (*ApathyMouseScrollFn)(int delta);          /* +1 up, -1 down */
            typedef void (*ApathyNotifyFn)(const char* msg);

            struct ApathyApi {
                ApathyLogFn         log;
                ApathySendKeyFn     send_key;
                ApathyMouseButtonFn mouse_button;
                ApathyMouseScrollFn mouse_scroll;
                ApathyNotifyFn      notify;
            };

            #if defined(_MSC_VER)
            #  define APATHY_EXPORT __declspec(dllexport)
            #else
            #  define APATHY_EXPORT __attribute__((visibility("default")))
            #endif

            /* Scripts must export these three functions: */
            #ifdef __cplusplus
            extern "C" {
            #endif
            APATHY_EXPORT void apathy_init  (ApathyApi* api); /* called once after loading      */
            APATHY_EXPORT void apathy_tick  ();               /* called every ~50 ms            */
            APATHY_EXPORT void apathy_unload();               /* called before the DLL unloads  */
            #ifdef __cplusplus
            }
            #endif
            """);
    }
}

