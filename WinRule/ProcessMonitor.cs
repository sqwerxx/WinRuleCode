using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinRule
{
    public static class ProcessMonitor
    {
        private static readonly Dictionary<int, string> BlockedProcessPaths = new Dictionary<int, string>();
        private static readonly Dictionary<int, AppRule> BlockedProcessRules = new Dictionary<int, AppRule>();
        private static readonly HashSet<int> InjectedProcesses = new HashSet<int>();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const uint MEM_COMMIT = 0x00001000;
        private const uint MEM_RESERVE = 0x00002000;
        private const uint PAGE_READWRITE = 0x04;

        private static void Log(string message) => System.Diagnostics.Debug.WriteLine(message);

        private static bool ProcessNameMatches(string ruleExecutableName, string actualProcessName)
        {
            string ruleName = Path.GetFileNameWithoutExtension(ruleExecutableName);
            string actualName = Path.GetFileNameWithoutExtension(actualProcessName);
            return ruleName.Equals(actualName, StringComparison.OrdinalIgnoreCase);
        }

        // Проверяет нужно ли инжектить DLL в процесс (есть ли хоть одна блокировка)
        private static bool ShouldInject(AppRule rule)
        {
            return rule.Camera == PermissionState.Deny ||
                   rule.Microphone == PermissionState.Deny ||
                   rule.Disk == PermissionState.Deny ||
                   rule.AdminRights == PermissionState.Deny ||
                   rule.Notifications == PermissionState.Deny ||
                   rule.Location == PermissionState.Deny;
        }

        private static void WriteProcessRuleToRegistry(string exePath, AppRule rule)
        {
            try
            {
                string safePath = exePath.Replace("\\", "_");
                string keyPath = @"Software\WinRule\BlockedApps\" + safePath;
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, true);
                if (key != null)
                {
                    key.SetValue("BlockCamera", rule.Camera == PermissionState.Deny ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("BlockMicrophone", rule.Microphone == PermissionState.Deny ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    // 🆕 НОВОЕ: геолокация и админка
                    key.SetValue("BlockLocation", rule.Location == PermissionState.Deny ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("BlockAdminRights", rule.AdminRights == PermissionState.Deny ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("BlockNotifications", rule.Notifications == PermissionState.Deny ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { }
        }
        private static void WriteBlockedPathsToRegistry(string exePath, List<string> blockedPaths)
        {
            try
            {
                string safePath = exePath.Replace("\\", "_");
                string keyPath = @"Software\WinRule\BlockedApps\" + safePath;
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, true);
                if (key != null)
                {
                    if (blockedPaths != null && blockedPaths.Count > 0)
                        key.SetValue("BlockedPaths", blockedPaths.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
                    else
                        try { key.DeleteValue("BlockedPaths", false); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"[WinRule] Ошибка записи BlockedPaths: {ex.Message}");
            }
        }

        public static async Task StartMonitoringAsync(CancellationToken token)
        {
            Log("[WinRule] ========================================");
            Log("[WinRule] ProcessMonitor запущен");
            Log("[WinRule] ========================================");

            CheckRunningProcesses();

            var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
            using var watcher = new ManagementEventWatcher(query);

            watcher.EventArrived += (sender, e) =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                    int parentPid = Convert.ToInt32(e.NewEvent.Properties["ParentProcessID"].Value);
                    string processName = e.NewEvent.Properties["ProcessName"].Value.ToString();
                    Log($"[WinRule] Обнаружен: {processName} (PID: {pid})");
                    HandleNewProcess(pid, parentPid, processName);
                }
                catch { }
            };

            watcher.Start();
            Log("[WinRule] WMI watcher запущен");

            // ФОНОВЫЙ СКАНЕР - ловит дочерние процессы
            var scannerTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { ScanAndInjectAllProcesses(); }
                    catch { }
                    await Task.Delay(500, token);
                }
            }, token);

            while (!token.IsCancellationRequested)
                await Task.Delay(100, token);

            watcher.Stop();
            try { await scannerTask; } catch { }
        }

        private static void ScanAndInjectAllProcesses()
        {
            var rules = RuleManager.LoadRules();
            if (rules.Count == 0) return;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    lock (InjectedProcesses)
                    {
                        if (InjectedProcesses.Contains(proc.Id)) continue;
                    }

                    string processName = proc.ProcessName;
                    foreach (var rule in rules)
                    {
                        if (ProcessNameMatches(rule.ExecutableName, processName) && ShouldInject(rule))
                        {
                            Log($"[WinRule] Scanner: инжектим в {processName} (PID: {proc.Id})");

                            string exePath;
                            try { exePath = proc.MainModule?.FileName; }
                            catch { continue; }

                            if (!string.IsNullOrEmpty(exePath))
                            {
                                lock (BlockedProcessPaths)
                                {
                                    BlockedProcessPaths[proc.Id] = exePath;
                                    BlockedProcessRules[proc.Id] = rule;
                                }
                                InjectBlockingDLL(proc.Id, rule);
                            }
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        private static void CheckRunningProcesses()
        {
            var rules = RuleManager.LoadRules();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string processName = proc.ProcessName;
                    foreach (var rule in rules)
                    {
                        if (ProcessNameMatches(rule.ExecutableName, processName) && ShouldInject(rule))
                        {
                            string exePath;
                            try { exePath = proc.MainModule?.FileName; }
                            catch { continue; }
                            if (!string.IsNullOrEmpty(exePath))
                            {
                                lock (BlockedProcessPaths)
                                {
                                    BlockedProcessPaths[proc.Id] = exePath;
                                    BlockedProcessRules[proc.Id] = rule;
                                }
                                InjectBlockingDLL(proc.Id, rule);
                            }
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        private static void HandleNewProcess(int pid, int parentPid, string processName)
        {
            var rules = RuleManager.LoadRules();

            foreach (var rule in rules)
            {
                if (ProcessNameMatches(rule.ExecutableName, processName) && ShouldInject(rule))
                {
                    AddProcessToNonPackaged(pid, rule);
                    InjectBlockingDLL(pid, rule);
                    return;
                }
            }

            lock (BlockedProcessPaths)
            {
                if (BlockedProcessPaths.ContainsKey(parentPid))
                {
                    var parentRule = BlockedProcessRules.ContainsKey(parentPid) ? BlockedProcessRules[parentPid] : null;
                    if (parentRule == null)
                        parentRule = rules.Find(r => r.ExecutablePath.Equals(BlockedProcessPaths[parentPid], StringComparison.OrdinalIgnoreCase));
                    if (parentRule != null)
                    {
                        AddProcessToNonPackaged(pid, parentRule);
                        InjectBlockingDLL(pid, parentRule);
                        return;
                    }
                }

                foreach (var rule in rules)
                {
                    if (ProcessNameMatches(rule.ExecutableName, processName) && ShouldInject(rule))
                    {
                        AddProcessToNonPackaged(pid, rule);
                        InjectBlockingDLL(pid, rule);
                        return;
                    }
                }
            }
        }

        private static void AddProcessToNonPackaged(int pid, AppRule rule)
        {
            try
            {
                Process proc;
                try { proc = Process.GetProcessById(pid); }
                catch { return; }
                string exePath = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
                lock (BlockedProcessPaths)
                {
                    BlockedProcessPaths[pid] = exePath;
                    BlockedProcessRules[pid] = rule;
                }
            }
            catch { }
        }

        private static void InjectBlockingDLL(int pid, AppRule rule)
        {
            try
            {
                lock (InjectedProcesses)
                {
                    if (InjectedProcesses.Contains(pid)) return;
                    InjectedProcesses.Add(pid);
                }

                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DeviceBlocker.dll");
                if (!File.Exists(dllPath)) return;

                string exePath = "";
                try { var proc = Process.GetProcessById(pid); exePath = proc.MainModule?.FileName ?? ""; }
                catch { exePath = rule.ExecutablePath; }

                if (!string.IsNullOrEmpty(exePath))
                {
                    WriteProcessRuleToRegistry(exePath, rule);
                    WriteBlockedPathsToRegistry(exePath, rule.BlockedPaths);
                }

                IntPtr processHandle = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                if (processHandle == IntPtr.Zero) return;

                try
                {
                    IntPtr kernel32 = GetModuleHandle("kernel32.dll");
                    IntPtr loadLibraryAddr = GetProcAddress(kernel32, "LoadLibraryA");
                    if (loadLibraryAddr == IntPtr.Zero) return;

                    byte[] dllPathBytes = System.Text.Encoding.ASCII.GetBytes(dllPath + "\0");
                    IntPtr allocMemAddress = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)dllPathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (allocMemAddress == IntPtr.Zero) return;
                    if (!WriteProcessMemory(processHandle, allocMemAddress, dllPathBytes, (uint)dllPathBytes.Length, out _)) return;

                    IntPtr threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibraryAddr, allocMemAddress, 0, out IntPtr threadId);
                    if (threadHandle != IntPtr.Zero)
                    {
                        Log($"[WinRule] !!! DLL инжектирована в PID {pid}!");
                        CloseHandle(threadHandle);
                    }
                }
                finally { CloseHandle(processHandle); }
            }
            catch { }
        }
    }
}