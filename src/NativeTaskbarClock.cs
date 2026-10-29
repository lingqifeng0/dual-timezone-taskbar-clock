using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NativeTaskbarClock
{
    internal static class Native
    {
        public const uint Access = 0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010;
        public const uint CommitReserve = 0x1000 | 0x2000, ReadWrite = 0x04, Release = 0x8000;
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr count, out UIntPtr written);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr size, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetExitCodeThread(IntPtr thread, out uint code);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    internal struct ClockOptions
    {
        public uint Size;
        public uint ControllerPid;
        public uint TopIsChina;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string LocalLabel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string ChinaLabel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string TimeFormat;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string DateFormat;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=96)] public string TopTemplate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=96)] public string BottomTemplate;
    }

    internal sealed class Controller : ApplicationContext
    {
        private const string SupportedSystemTraySha256 = "C5F5260A2A56C4D7B0EE25978AA06C387B5DDFFC57C5FE412BE429D1289913CE";
        private readonly string directory = AppDomain.CurrentDomain.BaseDirectory;
        private readonly NotifyIcon tray;
        private readonly Timer timer;
        private readonly System.Threading.EventWaitHandle stopEvent;
        private readonly System.Threading.EventWaitHandle showEvent;
        private readonly bool diagnostic;
        private Timer diagnosticTimer;
        private readonly Control dispatcher;
        private readonly SettingsPipe.Server settingsServer;
        private Process settingsProcess;
        private uint activePid;
        private bool automaticRetry = true;
        private readonly StartupRetry retry = new StartupRetry();

        public Controller(bool isDiagnostic, bool forceSettings, bool background,
                          System.Threading.EventWaitHandle stopSignal, System.Threading.EventWaitHandle showSignal)
        {
            diagnostic = isDiagnostic;
            stopEvent = stopSignal;
            showEvent = showSignal;
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("设置格式…", delegate { ShowSettings(); });
            menu.MenuItems.Add("重新应用时钟", delegate { ApplyFromMenu(); });
            menu.MenuItems.Add("编辑配置文件", delegate { EditSettings(); });
            menu.MenuItems.Add("说明", delegate { ShowHelp(); });
            menu.MenuItems.Add("退出并恢复原时钟", delegate { ExitThread(); });
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Information;
            tray.Text = "美中双时区时钟";
            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowSettings(); };
            tray.Visible = true;
            dispatcher = new Control();
            IntPtr dispatcherHandle = dispatcher.Handle;
            settingsServer = new SettingsPipe.Server(dispatcher, SaveFromPanel);
            timer = new Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { CheckExplorer(); };
            timer.Start();
            StartOrRetry(true);
            if (diagnostic) {
                diagnosticTimer = new Timer(); diagnosticTimer.Interval = 75000;
                diagnosticTimer.Tick += delegate { diagnosticTimer.Stop(); ExitThread(); };
                diagnosticTimer.Start();
            }
            if (!diagnostic && !background) {
                try {
                    ClockSettings settings = ClockSettings.Load(ConfigPath);
                    if (forceSettings || settings.FirstRun || settings.OpenOnStart) {
                        EventHandler open = null;
                        open = delegate { Application.Idle -= open; ShowSettings(); };
                        Application.Idle += open;
                    }
                } catch (Exception ex) { ShowError(ex); }
            }
        }

        private static uint TaskbarPid()
        {
            IntPtr hwnd = Native.FindWindow("Shell_TrayWnd", null);
            uint pid;
            if (hwnd == IntPtr.Zero || Native.GetWindowThreadProcessId(hwnd, out pid) == 0)
                throw new TaskbarNotReadyException("Windows 任务栏尚未准备好，将自动重试。");
            return pid;
        }

        private static ProcessModule FindModule(uint pid, string fileName)
        {
            try {
                using (Process process = Process.GetProcessById((int)pid))
                    foreach (ProcessModule module in process.Modules)
                        if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase)) return module;
            } catch (ArgumentException) {
                throw new TaskbarNotReadyException("任务栏进程正在重启，将自动重试。");
            } catch (InvalidOperationException) {
                throw new TaskbarNotReadyException("任务栏进程正在重启，将自动重试。");
            }
            return null;
        }

        private string HookName { get { return diagnostic ? "ClockProbeV2.dll" : "ClockNativeV2.dll"; } }
        private string HookPath { get { return Path.Combine(directory, HookName); } }
        private string ConfigPath { get { return Path.Combine(directory, "config.json"); } }

        private static ClockOptions BuildOptions(ClockSettings settings)
        {
            settings.Validate();
            ClockOptions options = new ClockOptions();
            options.Size = (uint)Marshal.SizeOf(typeof(ClockOptions));
            options.ControllerPid = (uint)Process.GetCurrentProcess().Id;
            options.LocalLabel = settings.LocalLabel;
            options.ChinaLabel = settings.ChinaLabel;
            options.TopIsChina = settings.TopIsChina ? 1u : 0u;
            options.TimeFormat = settings.TimeFormat;
            options.DateFormat = settings.DateFormat;
            options.TopTemplate = settings.TopTemplate;
            options.BottomTemplate = settings.BottomTemplate;
            return options;
        }

        private static byte[] StructBytes(ClockOptions options)
        {
            int size = Marshal.SizeOf(typeof(ClockOptions));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(options, pointer, false);
                byte[] bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); return bytes;
            } finally { Marshal.FreeHGlobal(pointer); }
        }

        private static uint RemoteCall(IntPtr process, IntPtr function, byte[] data)
        {
            IntPtr parameter = IntPtr.Zero, thread = IntPtr.Zero;
            bool finished = false;
            try {
                if (data != null) {
                    parameter = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)(uint)data.Length, Native.CommitReserve, Native.ReadWrite);
                    if (parameter == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    UIntPtr written;
                    if (!Native.WriteProcessMemory(process, parameter, data, (UIntPtr)(uint)data.Length, out written) || written.ToUInt64() != (ulong)data.Length)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                uint threadId;
                thread = Native.CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, function, parameter, 0, out threadId);
                if (thread == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                uint wait = Native.WaitForSingleObject(thread, 15000);
                if (wait != 0) throw new InvalidOperationException("任务栏响应超时；请退出程序后等待片刻再试。状态：" + wait);
                finished = true;
                uint code;
                if (!Native.GetExitCodeThread(thread, out code)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return code;
            } finally {
                if (thread != IntPtr.Zero) Native.CloseHandle(thread);
                // If a remote call timed out, its argument may still be in use.
                if (parameter != IntPtr.Zero && (thread == IntPtr.Zero || finished))
                    Native.VirtualFreeEx(process, parameter, UIntPtr.Zero, Native.Release);
            }
        }

        private static IntPtr ExportInRemote(uint pid, string moduleName, string exportName)
        {
            IntPtr localBase = Native.GetModuleHandleW(moduleName);
            if (localBase == IntPtr.Zero) throw new InvalidOperationException("本机缺少 " + moduleName);
            IntPtr localExport = Native.GetProcAddress(localBase, exportName);
            ProcessModule remote = FindModule(pid, moduleName);
            if (localExport == IntPtr.Zero || remote == null) throw new InvalidOperationException("无法定位系统调用：" + exportName);
            long offset = localExport.ToInt64() - localBase.ToInt64();
            if (offset < 0 || offset >= remote.ModuleMemorySize) throw new InvalidOperationException("系统调用位置不在预期模块内。");
            return new IntPtr(remote.BaseAddress.ToInt64() + offset);
        }

        private IntPtr HookExportInRemote(uint pid, string exportName)
        {
            ProcessModule remote = FindModule(pid, HookName);
            if (remote == null || !IsSameHook(remote.FileName))
                throw new InvalidOperationException("任务栏中已有不同版本的时钟模块。请退出时钟并重启 Windows 资源管理器后重试。");
            IntPtr local = Native.LoadLibraryW(HookPath);
            if (local == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                IntPtr fn = Native.GetProcAddress(local, exportName);
                if (fn == IntPtr.Zero) throw new InvalidOperationException("缺少模块入口：" + exportName);
                long offset = fn.ToInt64() - local.ToInt64();
                if (offset < 0 || offset >= remote.ModuleMemorySize) throw new InvalidOperationException("模块入口位置无效。");
                return new IntPtr(remote.BaseAddress.ToInt64() + offset);
            } finally { Native.FreeLibrary(local); }
        }

        private bool IsSameHook(string loadedPath)
        {
            if (string.Equals(Path.GetFullPath(loadedPath), Path.GetFullPath(HookPath), StringComparison.OrdinalIgnoreCase)) return true;
            using (SHA256 sha = SHA256.Create())
            using (FileStream first = File.OpenRead(loadedPath))
            using (FileStream second = File.OpenRead(HookPath))
                return sha.ComputeHash(first).SequenceEqual(sha.ComputeHash(second));
        }

        private void Apply() { Apply(ClockSettings.Load(ConfigPath)); }

        private void Apply(ClockSettings settings)
        {
            ClockOptions options = BuildOptions(settings);
            if (!Environment.Is64BitProcess || !File.Exists(HookPath)) throw new InvalidOperationException("缺少 64 位时钟模块 " + HookName + "。");
            uint pid = TaskbarPid();
            ProcessModule systemTray = FindModule(pid, "SystemTray.dll");
            if (systemTray == null) throw new TaskbarNotReadyException("任务栏组件尚未加载，将自动重试。");
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(systemTray.FileName)) {
                string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(hash, SupportedSystemTraySha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("当前 Windows 任务栏版本未经验证。程序没有修改任务栏。\n当前组件 SHA-256：" + hash);
            }
            IntPtr process = Native.OpenProcess(Native.Access, false, pid);
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法访问任务栏进程");
            try {
                ProcessModule existing = FindModule(pid, HookName);
                if (existing == null) {
                    IntPtr loader = ExportInRemote(pid, "KernelBase.dll", "LoadLibraryW");
                    RemoteCall(process, loader, Encoding.Unicode.GetBytes(HookPath + "\0"));
                } else if (!IsSameHook(existing.FileName)) {
                    throw new InvalidOperationException("已有不同版本的同名时钟模块。请退出时钟并重启 Windows 资源管理器后重试。");
                }
                IntPtr function = HookExportInRemote(pid, "InstallClockHook");
                uint status = RemoteCall(process, function, StructBytes(options));
                if (status != 0) throw new InvalidOperationException("无法启用原生时钟修改，状态码：" + status + "。任务栏不会使用新的显示。\n状态 2/4 表示版本不匹配，状态 3/5 可能表示已有冲突的时钟修改。");
                activePid = pid;
                TriggerClockRefresh();
            } finally { Native.CloseHandle(process); }
        }

        private void StopHook()
        {
            uint pid = activePid;
            activePid = 0;
            if (pid == 0) return;
            IntPtr process = Native.OpenProcess(Native.Access, false, pid);
            if (process == IntPtr.Zero) return; // Explorer may already have exited.
            try {
                uint status = RemoteCall(process, HookExportInRemote(pid, "UninstallClockHook"), null);
                if (status != 0) throw new InvalidOperationException("未能确认时钟完全恢复，状态码：" + status + "。可重启资源管理器恢复。");
            } finally { Native.CloseHandle(process); TriggerClockRefresh(); }
        }

        private static void TriggerClockRefresh()
        {
            // The taskbar watches this key for changes. Use a fresh value name
            // and immediately remove only the value created by this call.
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\TimeDate\AdditionalClocks", true)) {
                    if (key == null) return;
                    string name = "_DualZoneClockRefresh_" + Guid.NewGuid().ToString("N");
                    key.SetValue(name, "", RegistryValueKind.String);
                    key.DeleteValue(name, false);
                }
            } catch { /* The clock will refresh at its next normal tick. */ }
        }

        private void ApplyFromMenu()
        {
            try { StopHook(); StartOrRetry(true); }
            catch (Exception ex) { automaticRetry = false; ShowError(ex); }
        }

        private void StartOrRetry(bool notifyWaiting)
        {
            Exception failure;
            RetryOutcome outcome = retry.Attempt(Apply, out failure);
            automaticRetry = outcome != RetryOutcome.Failed;
            if (outcome == RetryOutcome.Failed) { ShowError(failure); return; }
            if (outcome == RetryOutcome.Waiting) {
                if (!diagnostic && notifyWaiting)
                    tray.ShowBalloonTip(4000, "等待 Windows 任务栏", "程序会自动重试，无需再次启动。", ToolTipIcon.Info);
                return;
            }
            if (diagnostic) File.WriteAllText(Path.Combine(directory, "diagnostic-result.txt"), "enabled", Encoding.UTF8);
            else tray.ShowBalloonTip(4000, "美中双时区时钟已启用", "任务栏可能在下一分钟刷新。右键本图标可设置或退出。", ToolTipIcon.Info);
        }

        private void ShowSettings()
        {
            try {
                if (settingsProcess != null && !settingsProcess.HasExited) {
                    settingsProcess.Refresh();
                    IntPtr window = settingsProcess.MainWindowHandle;
                    if (window != IntPtr.Zero) { Native.ShowWindow(window, 9); Native.SetForegroundWindow(window); }
                    return;
                }
                if (settingsProcess != null) settingsProcess.Dispose();
                ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--panel");
                start.WorkingDirectory = directory;
                start.UseShellExecute = false;
                settingsProcess = Process.Start(start);
            } catch (Exception ex) { ShowError(ex); }
        }

        private void SaveFromPanel(ClockSettings candidate)
        {
            candidate.Validate();
            ClockSettings previous = ClockSettings.Load(ConfigPath);
            bool wasActive = activePid != 0;
            bool wasPending = retry.Pending;
            bool existed = File.Exists(ConfigPath);
            bool configWritten = false;
            StopHook();
            RetryOutcome outcome;
            try {
                Exception failure;
                outcome = retry.Attempt(delegate { Apply(candidate); }, out failure);
                if (outcome == RetryOutcome.Failed) throw failure;
                candidate.Save(ConfigPath);
                configWritten = true;
                Autostart.SetEnabled(Application.ExecutablePath, candidate.StartOnLogin);
                automaticRetry = true;
            } catch (Exception ex) {
                string rollbackMessage = "";
                try {
                    if (configWritten) {
                        if (existed) previous.Save(ConfigPath);
                        else if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
                    }
                } catch (Exception rollback) { rollbackMessage += "\n恢复之前的配置文件时也遇到问题：" + rollback.Message; }
                try {
                    StopHook();
                    retry.Cancel();
                    if (wasActive || wasPending) {
                        Exception failure;
                        RetryOutcome restored = retry.Attempt(delegate { Apply(previous); }, out failure);
                        automaticRetry = restored != RetryOutcome.Failed;
                        if (restored == RetryOutcome.Failed) throw failure;
                    }
                }
                catch (Exception rollback) { rollbackMessage += "\n恢复之前设置时也遇到问题：" + rollback.Message; }
                throw new InvalidOperationException("设置未保存：" + ex.Message + rollbackMessage, ex);
            }
            tray.ShowBalloonTip(4000, "设置已保存", outcome == RetryOutcome.Waiting
                ? "任务栏尚未准备好，程序会自动重试。"
                : "任务栏可能在下一分钟显示新格式。", ToolTipIcon.Info);
        }

        private void EditSettings()
        {
            try { Process.Start("notepad.exe", "\"" + ConfigPath + "\""); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowHelp()
        {
            MessageBox.Show("双击托盘图标，或右键选择“设置格式”，可以修改时区顺序和两行内容。\n\n任务栏通常会在下一分钟刷新；退出时请选择“退出并恢复原时钟”。", "美中双时区时钟", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowError(Exception ex)
        {
            if (diagnostic) {
                File.WriteAllText(Path.Combine(directory, "diagnostic-result.txt"), "error: " + ex, Encoding.UTF8);
                return;
            }
            MessageBox.Show(ex.Message, "美中双时区时钟", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void CheckExplorer()
        {
            if (stopEvent.WaitOne(0)) { ExitThread(); return; }
            if (showEvent.WaitOne(0)) ShowSettings();
            if (!automaticRetry) return;
            try {
                if (retry.Pending) { StartOrRetry(false); return; }
                if (activePid == 0) return;
                uint now = TaskbarPid();
                if (now != activePid) {
                    // The old Explorer can disappear between OpenProcess and module lookup.
                    try { StopHook(); }
                    catch (TaskbarNotReadyException) { }
                    StartOrRetry(true);
                    return;
                }
                if (diagnostic) {
                    IntPtr process = Native.OpenProcess(Native.Access, false, activePid);
                    if (process != IntPtr.Zero) {
                        try {
                            uint counts = RemoteCall(process, HookExportInRemote(activePid, "HookDiagnosticCounts"), null);
                            uint time = RemoteCall(process, HookExportInRemote(activePid, "HookDiagnosticTimeRva"), null);
                            uint date = RemoteCall(process, HookExportInRemote(activePid, "HookDiagnosticDateRva"), null);
                            File.AppendAllText(Path.Combine(directory, "diagnostic-result.txt"), "\ncounts=" + counts.ToString("X8") + " time=" + time.ToString("X") + " date=" + date.ToString("X"), Encoding.UTF8);
                        } finally { Native.CloseHandle(process); }
                    }
                }
            } catch (Exception ex) {
                // Explorer can be absent while it restarts.
                if (ex is TaskbarNotReadyException) return;
                automaticRetry = false; ShowError(ex);
            }
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            try { StopHook(); } catch (Exception ex) { ShowError(ex); }
            try { if (settingsProcess != null && !settingsProcess.HasExited) settingsProcess.CloseMainWindow(); } catch { }
            if (settingsProcess != null) settingsProcess.Dispose();
            settingsServer.Dispose(); dispatcher.Dispose();
            tray.Visible = false; tray.Dispose(); timer.Dispose();
            if (diagnosticTimer != null) diagnosticTimer.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        private const string StopEventName = @"Local\DualZoneNativeTaskbarClock.Stop";
        private const string ShowEventName = @"Local\DualZoneNativeTaskbarClock.ShowV2";
        [STAThread] private static void Main(string[] args)
        {
            if (args.Contains("--panel")) {
                try {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                    ClockSettings settings = ClockSettings.Load(configPath);
                    settings.StartOnLogin = Autostart.IsEnabled(Application.ExecutablePath);
                    Application.Run(new SettingsForm(settings, SettingsPipe.Apply));
                } catch (Exception ex) { MessageBox.Show(ex.Message, "双时区时钟设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                return;
            }
            if (args.Length == 1 && args[0] == "--stop") {
                try { using (var signal = System.Threading.EventWaitHandle.OpenExisting(StopEventName)) signal.Set(); }
                catch (System.Threading.WaitHandleCannotBeOpenedException) { }
                return;
            }
            bool created;
            using (var mutex = new System.Threading.Mutex(true, @"Local\DualZoneNativeTaskbarClock", out created)) {
                if (!created) {
                    try { using (var signal = System.Threading.EventWaitHandle.OpenExisting(ShowEventName)) signal.Set(); }
                    catch (System.Threading.WaitHandleCannotBeOpenedException) { MessageBox.Show("旧版时钟正在运行，请先从通知区域退出旧版。", "美中双时区时钟"); }
                    return;
                }
                using (var stopSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, StopEventName)) {
                    using (var showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ShowEventName)) {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new Controller(args.Contains("--diagnostic"), args.Contains("--settings"), args.Contains("--background"), stopSignal, showSignal));
                    }
                }
            }
        }
    }
}
