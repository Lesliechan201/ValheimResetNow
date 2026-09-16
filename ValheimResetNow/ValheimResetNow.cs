using BepInEx;
using HarmonyLib;
using Jotunn;
using Jotunn.Utils;
using UnityEngine.SceneManagement;

namespace ValheimResetNow
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    [BepInDependency("MidnightsFX.StarLevelSystem", BepInDependency.DependencyFlags.HardDependency)]
    internal class ValheimResetNow : BaseUnityPlugin
    {
        public const string PluginGUID = "Leslie.ValheimResetNow";
        public const string PluginName = "ValheimResetNow";
        public const string PluginVersion = "0.1.0";

        internal static ValheimResetNow Instance;

        private void Awake()
        {
            Instance = this;

            new VRNConfig(Config);
            BossStone.Register();

            new Harmony(PluginGUID).PatchAll();

            SceneManager.sceneUnloaded += _ =>
            {
                BossStonePlacement.Reset();
                BossStonePlacement.BossStoneSweep.Reset();
                BossStoneReset.Reset();
                StoneRelay.Reset();
                RenewalEffects.Reset();
                CharacterResetLog.Unload();
            };

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded");
        }
    }
}