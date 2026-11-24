using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WinRule
{
    public static class PermissionHelper
    {
        // --- 1. КАМЕРА И МИКРОФОН (Нативная изоляция через NonPackaged) ---
        public static void SetHardwarePermission(string exePath, string hardwareType, PermissionState state)
        {
            string regBase = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{hardwareType}\NonPackaged";

            // Windows преобразует пути в реестре, заменяя спецсимволы на решетки
            string safePath = exePath.Replace(@"\", "#").Replace(":", "#");
            string regPath = $@"{regBase}\{safePath}";

            if (state == PermissionState.Deny)
            {
                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                key?.SetValue("Value", "Deny", RegistryValueKind.String);
            }
            else
            {
                // При Allow или Ask удаляем ключ, возвращая дефолтное поведение Windows
                using var baseKey = Registry.CurrentUser.OpenSubKey(regBase, true);
                baseKey?.DeleteSubKeyTree(safePath, false);
            }
        }

        // --- 2. БЛОКИРОВКА ДИСКА (Controlled Folder Access) ---
        public static void SetDiskPermission(string exePath, PermissionState state)
        {
            try
            {
                if (state == PermissionState.Deny)
                    ExecutePowerShell($"Add-MpPreference -ControlledFolderAccessBlockedApplications '{exePath}'");
                else if (state == PermissionState.Allow)
                    ExecutePowerShell($"Remove-MpPreference -ControlledFolderAccessBlockedApplications '{exePath}'");
            }
            catch { }
        }

        // --- 3. БЛОКИРОВКА АДМИН-ПРАВ (Software Restriction Policies) ---
        public static void SetAdminPermission(string exePath, string exeName, PermissionState state)
        {
            try
            {
                string regPath = @"Software\Policies\Microsoft\Windows\Safer\CodeIdentifiers\0\Paths";
                using var key = Registry.CurrentUser.CreateSubKey(regPath);
                string ruleName = $"WinRule_BlockAdmin_{exeName.Replace(".exe", "")}";

                if (state == PermissionState.Deny)
                {
                    using var ruleKey = key.CreateSubKey(ruleName);
                    ruleKey.SetValue("ItemData", exePath);
                    ruleKey.SetValue("SaferFlags", 0);
                }
                else
                {
                    key.DeleteSubKeyTree(ruleName, false);
                }
            }
            catch { }
        }

        private static void ExecutePowerShell(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
        }
    }
}