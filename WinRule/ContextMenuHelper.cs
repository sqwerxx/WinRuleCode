using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinRule
{
    public static class ContextMenuHelper
    {
        public static void Register()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                string menuText = "Настроить Разрешения";

                // Пишем в HKCU, чтобы не требовать прав админа для установки меню
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\exefile\shell\WinRule");
                if (key != null)
                {
                    key.SetValue("", menuText);
                    key.SetValue("Icon", $"\"{exePath}\"");

                    using var commandKey = key.CreateSubKey("command");
                    commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");
                }
            }
            catch { }
        }

        public static void Unregister()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\exefile\shell\WinRule", false);
            }
            catch { }
        }
    }
}