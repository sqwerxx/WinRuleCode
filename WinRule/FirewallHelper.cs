using System;
using System.Diagnostics;
using System.IO;

namespace WinRule
{
    public static class FirewallHelper
    {
        public static void ApplyNetworkRule(string exePath, string exeName, PermissionState state)
        {
            string ruleName = $"WinRule_Net_{exeName.Replace(".exe", "")}";

            // Сначала удаляем старое правило, чтобы не плодить дубликаты при изменении
            ExecuteNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

            if (state == PermissionState.Deny)
            {
                ExecuteNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block program=\"{exePath}\" enable=yes");
            }
            else if (state == PermissionState.Allow)
            {
                ExecuteNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=allow program=\"{exePath}\" enable=yes");
            }
        }

        private static void ExecuteNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    Verb = "runas", // ВЫЗЫВАЕТ UAC (Запрос прав админа)
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new UnauthorizedAccessException("Для изменения правил брандмауэра требуются права администратора.");
            }
        }
    }
}