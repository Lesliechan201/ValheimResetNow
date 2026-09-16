using BepInEx;
using BepInEx.Configuration;
using System.IO;
using UnityEngine;

namespace ValheimResetNow
{
    internal class VRNConfig
    {
        public static ConfigFile cfg;

        private const string SectionStone = "CryptStone";
        private const string SectionReset = "CryptStone.Reset";

        internal static ConfigEntry<KeyCode> TakeTrophyKey;
        internal static ConfigEntry<bool> AllowForceScheduledReset;
        internal static ConfigEntry<bool> AlwaysAllowTrophyResets;

        internal const float ResetSearchRadius = 64f;
        internal const float ResetDelaySeconds = 3f;
        internal const float SealSeconds = 20f;
        internal const float ReadIntervalSeconds = 60f;

        public VRNConfig(ConfigFile cf)
        {
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            SetupMainFileWatcher();
        }

        internal static void SetupMainFileWatcher()
        {
            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Path = Path.GetDirectoryName(cfg.ConfigFilePath);
            watcher.Filter = Path.GetFileName(cfg.ConfigFilePath);
            watcher.Changed += OnConfigFileChanged;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(cfg.ConfigFilePath)) { return; }
            cfg.Reload();
        }

        public static ConfigEntry<T> BindServerConfig<T>(string category, string key, T value, string description)
        {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description, null,
                    new ConfigurationManagerAttributes { IsAdminOnly = true }));
        }

        public static ConfigEntry<T> BindClientConfig<T>(string category, string key, T value, string description)
        {
            return cfg.Bind(category, key, value, new ConfigDescription(description));
        }

        private void CreateConfigValues(ConfigFile config)
        {
            TakeTrophyKey = BindClientConfig(SectionStone, "TakeTrophyKey", KeyCode.LeftAlt, "Key to take trophy");

            AllowForceScheduledReset = BindServerConfig(SectionReset, "AllowForceScheduledReset", true, "Flag to allow forcing scheduled resets that are ready.");

            AlwaysAllowTrophyResets = BindServerConfig(SectionReset, "AlwaysAllowTrophyResets", false, "Always allow trophy resets.");
        }
    }
}
