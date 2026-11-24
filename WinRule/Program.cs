using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinRule
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            bool isSilent = args.Contains("--silent");
            string targetFilePath = args.FirstOrDefault(a => !a.StartsWith("-") && a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (isSilent)
            {
                bool createdNew;
                using var mutex = new Mutex(true, "WinRule_Background_Mutex", out createdNew);
                if (!createdNew) return;
                Application.Run(new BackgroundAppContext());
            }
            else
            {
                Application.Run(new Разрешения(targetFilePath));
            }
        }
    }

    public class BackgroundAppContext : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private CancellationTokenSource _cts;

        public BackgroundAppContext()
        {
            _cts = new CancellationTokenSource();

            ApplyAllStaticRulesSilently();

            _ = Task.Run(() => ProcessMonitor.StartMonitoringAsync(_cts.Token), _cts.Token);

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "WinRule: Защита активна (Умный мониторинг)",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть настройки", null, (s, e) => OpenMainForm());
            menu.Items.Add("Выход", null, (s, e) => ExitApp());

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.MouseDoubleClick += (s, e) => OpenMainForm();
        }

        private void ApplyAllStaticRulesSilently()
        {
            try
            {
                var rules = RuleManager.LoadRules();
                foreach (var rule in rules)
                {
                    if (File.Exists(rule.ExecutablePath))
                    {
                        FirewallHelper.ApplyNetworkRule(rule.ExecutablePath, rule.ExecutableName, rule.Network);
                        PermissionHelper.SetDiskPermission(rule.ExecutablePath, rule.Disk);
                        PermissionHelper.SetAdminPermission(rule.ExecutablePath, rule.ExecutableName, rule.AdminRights);

                        PermissionHelper.SetHardwarePermission(rule.ExecutablePath, "webcam", rule.Camera);
                        PermissionHelper.SetHardwarePermission(rule.ExecutablePath, "microphone", rule.Microphone);
                    }
                }
            }
            catch { }
        }

        private void OpenMainForm()
        {
            _trayIcon.Visible = false;
            var form = new Разрешения(null);
            form.FormClosed += (s, e) => ExitApp();
            form.Show();
        }

        private void ExitApp()
        {
            _cts?.Cancel();
            _trayIcon.Dispose();
            Application.Exit();
        }
    }
}