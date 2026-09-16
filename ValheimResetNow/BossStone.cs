using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimResetNow
{

    internal static class BossStone
    {
        internal const string NamePrefix = "VRN_BossStone_";

        private static readonly Dictionary<string, string> bossStones = new Dictionary<string, string>
        {
            { "TrophyEikthyr", "BossStone_Eikthyr" },
            { "TrophyTheElder", "BossStone_TheElder" },
            { "TrophyBonemass", "BossStone_Bonemass" },
            { "TrophyDragonQueen", "BossStone_DragonQueen" },
            { "TrophyGoblinKing", "BossStone_Yagluth" },
            { "TrophySeekerQueen", "BossStone_TheQueen" },
            { "TrophyFader", "BossStone_Fader" }
        };

        private static readonly Dictionary<int, string> trophies = new Dictionary<int, string>
        {
            { "Crypt2".GetStableHashCode(), "TrophyEikthyr" },
            { "Crypt3".GetStableHashCode(), "TrophyEikthyr" },
            { "Crypt4".GetStableHashCode(), "TrophyEikthyr" },
            { "Hildir_crypt".GetStableHashCode(), "TrophyEikthyr" },

            { "SunkenCrypt4".GetStableHashCode(), "TrophyTheElder" },

            { "Hildir_cave".GetStableHashCode(), "TrophyBonemass" },
            { "MountainCave02".GetStableHashCode(), "TrophyBonemass" },

            { "Hildir_plainsfortress".GetStableHashCode(), "TrophyDragonQueen" },

            { "Mistlands_DvergrTownEntrance1".GetStableHashCode(), "TrophyGoblinKing" },
            { "Mistlands_DvergrTownEntrance2".GetStableHashCode(), "TrophyGoblinKing" },

            { "CharredFortress".GetStableHashCode(), "TrophySeekerQueen" }

        };

        internal static string TrophyFor(string locationName)
        {
            string trophy;
            if (!string.IsNullOrEmpty(locationName) &&
                trophies.TryGetValue(locationName.GetStableHashCode(), out trophy))
            {
                return trophy;
            }
            return null;
        }

        private static IEnumerable<string> RequiredTrophies()
        {
            HashSet<string> wanted = new HashSet<string>();
            foreach (KeyValuePair<int, string> pair in trophies)
            {
                if (!string.IsNullOrEmpty(pair.Value)) { wanted.Add(pair.Value); }
            }
            return wanted;
        }

        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

        private static readonly HashSet<int> ourHashes = new HashSet<int>();

        internal static bool Ready { get { return prefabs.Count > 0; } }

        internal static bool IsOurs(int prefabHash)
        {
            return ourHashes.Contains(prefabHash);
        }

        internal static GameObject For(string trophy)
        {
            GameObject prefab;
            return prefabs.TryGetValue(trophy ?? string.Empty, out prefab) ? prefab : null;
        }

        internal static void Register()
        {
            PrefabManager.OnPrefabsRegistered += EnsureRegistered;
        }

        private static void EnsureRegistered()
        {
            foreach (string trophy in RequiredTrophies())
            {
                GameObject prefab;
                if (!prefabs.TryGetValue(trophy, out prefab) || prefab == null)
                {
                    prefab = Build(trophy);
                    if (prefab == null) { continue; }
                    prefabs[trophy] = prefab;
                    ourHashes.Add(prefab.name.GetStableHashCode());
                }

                PrefabManager.Instance.RegisterToZNetScene(prefab);
            }
        }

        private const float Scale = 0.5f;

        private static GameObject Build(string trophy)
        {
            string baseName = bossStones[trophy];

            GameObject clone = PrefabManager.Instance.CreateClonedPrefab(NamePrefix + trophy, baseName);
            if (clone == null)
            {
                return null;
            }

            clone.transform.localScale = Vector3.one * Scale;

            PrefabManager.Instance.AddPrefab(clone);
            return clone;
        }

    }

    internal static class BossStoneReset
    {
        internal static void Reset()
        {
            lastPrime = float.NegativeInfinity;
            primed.Clear();
        }

        [HarmonyPatch(typeof(ItemStand), nameof(ItemStand.Interact))]
        internal static class InteractPatch
        {
            [HarmonyPrefix]
            private static bool AltUse(ItemStand __instance, Humanoid user, bool hold, bool alt, ref bool __result)
            {
                bool take = TakeHeld();

                if (hold)
                {
                    if (!alt && !take) { return true; }
                    __result = false;
                    return false;
                }

                ZNetView view = OurView(__instance);
                if (view == null) { return true; }

                Player player = user as Player;
                if (player == null || player != Player.m_localPlayer) { return true; }

                if (!view.IsOwner()) { view.ClaimOwnership(); }

                __result = true;

                if (alt) { Use(player, __instance, view.GetZDO()); return false; }
                if (take) { Take(player, __instance); return false; }

                if (__instance.HaveAttachment()) { __result = false; return false; }

                return true;
            }

            private static bool TakeHeld()
            {
                KeyCode key = VRNConfig.TakeTrophyKey.Value;
                return key != KeyCode.None && Input.GetKey(key);
            }
        }

        [HarmonyPatch(typeof(ItemStand), nameof(ItemStand.GetHoverText))]
        internal static class HoverPatch
        {
            [HarmonyPostfix]
            private static void Append(ItemStand __instance, ref string __result)
            {
                if (string.IsNullOrEmpty(__result)) { return; }

                ZDO zdo = StoneZdo(__instance);
                if (zdo == null) { return; }

                string tail = __instance.HaveAttachment() ? HoldToActivate(__result) : __result;

                __result = StatusLine(zdo, __instance) + "\n\n" + tail;
            }
        }

        [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.GetHoverText))]
        internal static class LoreHoverPatch
        {
            [HarmonyPostfix]
            private static void Replace(RuneStone __instance, ref string __result)
            {
                ZDO zdo = RuneZdo(__instance);
                if (zdo == null) { return; }

                __result = Status(zdo) + ClaimPrompt(zdo) + "\n\n" + __result;
            }
        }

        [HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
        internal static class LoreInteractPatch
        {
            [HarmonyPrefix]
            private static bool ReadInstead(RuneStone __instance, Humanoid character, bool hold, bool alt,
                                            ref bool __result)
            {
                if (hold) { return true; }

                ZDO zdo = RuneZdo(__instance);
                if (zdo == null) { return true; }

                Player player = character as Player;
                if (player == null || player != Player.m_localPlayer) { return true; }

                if (alt)
                {
                    player.DoInteractAnimation(__instance.gameObject);

                    StoneRelay.Ask(zdo.m_uid, StoneRelay.ModeScheduled);
                    __result = false;
                    return false;
                }

                Read(player, zdo);
                return true;
            }
        }

        private static string ClaimPrompt(ZDO zdo)
        {
            if (!VRNConfig.AllowForceScheduledReset.Value) { return string.Empty; }

            long schedAt = zdo.GetLong(StoneRelay.SchedAtKey, -1L);
            if (schedAt < 0L) { return string.Empty; }
            if (schedAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { return string.Empty; }

            return "\n[<color=yellow><b>" + AltKey + " + " + UseKey + "</b></color>] Force scheduled reset";
        }

        private static string UseKey { get { return Localization.instance.Localize("$KEY_Use"); } }
        private static string AltKey { get { return Localization.instance.Localize("$KEY_AltPlace"); } }

        private static ZDO RuneZdo(RuneStone rune)
        {
            if (rune == null) { return null; }

            ZNetView view = rune.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid()) { return null; }

            ZDO zdo = view.GetZDO();
            if (zdo == null || !BossStone.IsOurs(zdo.GetPrefab())) { return null; }
            return zdo;
        }

        private static void Read(Player player, ZDO zdo)
        {
            if (StoneRelay.Fresh(zdo)) { return; }

            StoneRelay.Ask(zdo.m_uid, StoneRelay.ModeStatus);
        }

        private static void Use(Player player, ItemStand stand, ZDO zdo)
        {
            if (!stand.HaveAttachment()) return;

            StoneRelay.Ask(zdo.m_uid, StoneRelay.ModeReset);
        }

        internal static void Answered(ZDOID stone, int state, float seconds, string text)
        {
            Player player = Player.m_localPlayer;

            switch (state)
            {
                case StoneRelay.StateDone:
                    if (player != null) { player.Message(MessageHud.MessageType.Center, "Reset Success"); }
                    return;

                case StoneRelay.StateBusy:
                    if (player != null) { player.Message(MessageHud.MessageType.Center, "Other Reset In Progress"); }
                    return;

                case StoneRelay.StateFailed:
                    if (player != null)
                    {
                        player.Message(MessageHud.MessageType.Center,
                            string.IsNullOrEmpty(text) ? "Reset Failed" : text);
                    }
                    return;

                case StoneRelay.StateCooling:
                    if (player != null)
                    {
                        player.Message(MessageHud.MessageType.Center,
                            seconds > 0f
                                ? "Trophy reset already used " + Countdown(seconds)
                                : "Trophy reset already used");
                    }
                    return;
            }

        }

        private static string StatusLine(ZDO zdo, ItemStand stand)
        {
            string status = Status(zdo);

            return status + "\n" + Prompt(stand);
        }

        private enum StoneState { Unread, Timed }

        private static StoneState StateOf(ZDO zdo)
        {
            if (zdo.GetLong(StoneRelay.ReadyAtKey, Unstamped) == Unstamped) { return StoneState.Unread; }
            return StoneState.Timed;
        }

        private static string Status(ZDO zdo)
        {
            switch (StateOf(zdo))
            {
                case StoneState.Unread:
                    Prime(zdo.m_uid);
                    return "<color=#b0b0b0>Reading the stone...</color>";

                default:
                    return Clocks(zdo);
            }
        }

        private static string Clocks(ZDO zdo)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long left = zdo.GetLong(StoneRelay.ReadyAtKey, Unstamped) - now;

            string line = left > 0L
                ? "<color=#d0c0a0>" + Countdown(left) + " until trophy reset available</color>"
                : "<color=#00ff00>Trophy reset available</color>";

            long schedAt = zdo.GetLong(StoneRelay.SchedAtKey, -1L);
            if (schedAt < 0L) { return line; }

            long due = schedAt - now;
            return line + (due > 0L
                ? "\n<color=#8fb8e0>" + Countdown(due) + " until scheduled reset</color>"
                : "\n<color=#8fb8e0>Scheduled reset due</color>");
        }

        private const long Unstamped = long.MinValue;

        private static void Prime(ZDOID id)
        {
            if (primed.Contains(id)) { return; }
            if (Time.time - lastPrime < 1f) { return; }

            primed.Add(id);
            lastPrime = Time.time;
            StoneRelay.Ask(id, StoneRelay.ModeStatus);
        }

        private static readonly HashSet<ZDOID> primed = new HashSet<ZDOID>();
        private static float lastPrime = float.NegativeInfinity;

        private static string HoldToActivate(string hover)
        {
            string tap = Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>]");
            if (!hover.Contains(tap)) { return hover; }

            string held = Localization.instance.Localize("[<color=yellow><b>$ui_hold $KEY_Use</b></color>]");
            return hover.Replace(tap, held);
        }

        private static string Prompt(ItemStand stand)
        {
            string renew = "[<color=yellow><b>" + AltKey + " + " + UseKey + "</b></color>] Reset with Trophy";

            KeyCode take = VRNConfig.TakeTrophyKey.Value;
            if (take == KeyCode.None || !stand.HaveAttachment()) { return renew; }

            return renew + "\n[<color=yellow><b>" + take + " + " + UseKey + "</b></color>] Take Trophy";
        }

        private static void Take(Player player, ItemStand stand)
        {
            if (RenewalEffects.IsSealed(stand.transform.position)) { return; }

            if (!stand.HaveAttachment()) return;

            ZNetView view = OurView(stand);
            if (view == null) { return; }
            if (!view.IsOwner()) { view.ClaimOwnership(); }

            stand.DropItem();
        }

        private static string Countdown(double seconds)
        {
            switch (System.TimeSpan.FromSeconds(seconds))
            {
                case System.TimeSpan span when span.TotalDays >= 1d:
                    return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes:00}m {span.Seconds:00}s";

                case System.TimeSpan span when span.TotalHours >= 1d:
                    return $"{(int)span.TotalHours}h {span.Minutes:00}m {span.Seconds:00}s";

                case System.TimeSpan span when span.TotalMinutes >= 1d:
                    return $"{span.Minutes}m {span.Seconds:00}s";

                default:
                    return Mathf.Max(1, Mathf.CeilToInt((float)seconds)) + "s";
            }
        }

        private static ZNetView OurView(ItemStand stand)
        {
            if (stand == null) { return null; }

            ZNetView view = stand.m_nview != null ? stand.m_nview : stand.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid()) { return null; }

            ZDO zdo = view.GetZDO();
            return zdo != null && BossStone.IsOurs(zdo.GetPrefab()) ? view : null;
        }

        private static ZDO StoneZdo(ItemStand stand)
        {
            ZNetView view = OurView(stand);
            return view != null ? view.GetZDO() : null;
        }
    }

    internal static class RenewalEffects
    {
        internal const string SoundRpc = "VRN_StoneSound";
        internal const string SealRpc = "VRN_StoneSeal";

        private static Vector3 sealPos;
        private static float sealRadius;
        private static float sealUntil = float.NegativeInfinity;

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<Vector3>(SoundRpc, Heard);
            rpc.Register<Vector3, float, float>(SealRpc, Sealed);
            rpc.Register<Vector3, Vector3, string, bool>(BlockerRpc, Blocked);
        }

        internal const string BlockerRpc = "VRN_StoneBlocker";

        private const float BlockerTellRadius = 20f;

        internal static void Blame(Vector3 stonePos, Vector3 blockerPos, string what, long asker)
        {
            if (ZRoutedRpc.instance == null) { return; }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, BlockerRpc,
                stonePos, blockerPos, what ?? string.Empty, false);

            if (asker != 0L)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(asker, BlockerRpc,
                    stonePos, blockerPos, what ?? string.Empty, true);
            }
        }

        private static void PingSelf(Vector3 position)
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZRoutedRpc.instance == null) { return; }

            position.y = player.transform.position.y;

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.m_id, "ChatMessage",
                position, (int)Talker.Type.Ping, UserInfo.GetLocalUser(), string.Empty);
        }

        private static void Blocked(long sender, Vector3 stonePos, Vector3 blockerPos, string what, bool ping)
        {
            Player player = Player.m_localPlayer;
            if (player == null) { return; }

            if (!Player.IsPlayerInRange(stonePos, BlockerTellRadius)) { return; }

            if (ping)
            {
                if (blockerPos != Vector3.zero) { PingSelf(blockerPos); }
                return;
            }

            player.Message(MessageHud.MessageType.Center,
                what.Length > 0 ? "Blocked by " + what : "Something nearby is blocking this");
        }

        internal static void Reset()
        {
            sealUntil = float.NegativeInfinity;
        }

        internal static void Seal(Vector3 center, float seconds)
        {
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, SealRpc,
                center, VRNConfig.ResetSearchRadius, seconds);
        }

        private static void Sealed(long sender, Vector3 center, float radius, float seconds)
        {
            if (seconds <= 0f)
            {
                sealUntil = float.NegativeInfinity;
                return;
            }

            sealPos = center;
            sealRadius = radius;
            sealUntil = Time.time + seconds;
        }

        internal static bool IsSealed(Vector3 point)
        {
            if (Time.time >= sealUntil) { return false; }

            Vector3 delta = point - sealPos;
            return delta.sqrMagnitude <= sealRadius * sealRadius;
        }

        [HarmonyPatch(typeof(Teleport), nameof(Teleport.Interact))]
        internal static class EntrancePatch
        {
            [HarmonyPrefix]
            private static bool Block(Teleport __instance, Humanoid character, bool hold, ref bool __result)
            {
                if (hold || character == null || character.InInterior()) { return true; }
                if (!IsSealed(__instance.transform.position)) { return true; }

                character.Message(MessageHud.MessageType.Center, "Blocked");
                __result = false;
                return false;
            }
        }

        internal static void Announce(long peer, Vector3 stonePos)
        {
            if (ZRoutedRpc.instance == null) { return; }

            ZRoutedRpc.instance.InvokeRoutedRPC(peer, SoundRpc, stonePos);
        }

        private static void Heard(long sender, Vector3 stonePos)
        {
            Player player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null) { return; }

            GameObject prefab = ZNetScene.instance.GetPrefab("sfx_bomblava_rocks");
            if (prefab == null) { return; }

            UnityEngine.Object.Instantiate(prefab, player.transform.position, Quaternion.identity);
        }
    }

}
