using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinRule
{
    public enum PermissionState { Ask, Allow, Deny }

    public class AppRule
    {
        public string ExecutablePath { get; set; } = "";
        public string ExecutableName { get; set; } = "";

        public PermissionState Network { get; set; } = PermissionState.Allow;
        public PermissionState Camera { get; set; } = PermissionState.Ask;
        public PermissionState Microphone { get; set; } = PermissionState.Ask;
        public PermissionState Disk { get; set; } = PermissionState.Allow;
        public PermissionState AdminRights { get; set; } = PermissionState.Allow;
        public PermissionState Notifications { get; set; } = PermissionState.Ask;

        // 🆕 НОВОЕ: Местоположение
        public PermissionState Location { get; set; } = PermissionState.Ask;

        public List<string> BlockedPaths { get; set; } = new List<string>();
        public List<string> AllowedPaths { get; set; } = new List<string>();
        public bool BlockAllWindowsDrive { get; set; } = false;
    }

    public static class RuleManager
    {
        private static readonly string RulesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinRule");
        private static readonly string RulesFile = Path.Combine(RulesDir, "rules.json");

        public static List<AppRule> LoadRules()
        {
            if (!File.Exists(RulesFile)) return new List<AppRule>();
            try
            {
                var json = File.ReadAllText(RulesFile);
                return JsonSerializer.Deserialize<List<AppRule>>(json) ?? new List<AppRule>();
            }
            catch { return new List<AppRule>(); }
        }

        public static void SaveRules(List<AppRule> rules)
        {
            Directory.CreateDirectory(RulesDir);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(RulesFile, JsonSerializer.Serialize(rules, options));
        }
    }
}