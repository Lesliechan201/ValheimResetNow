using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimResetNow
{
    internal static class StoneRelay
    {
        private const string AskRpc = "VRN_StoneAsk";
        private const string TellRpc = "VRN_StoneTell";

        internal const int ModeStatus = 0;
        internal const int ModeReset = 1;

        internal const int ModeScheduled = 3;

        internal const int StateCooling = 2;
        internal const int StateDone = 4;
        internal const int StateBusy = 5;
        internal const int StateFailed = 6;

        private static bool resetRunning;

        private static ZDOID committedStone = ZDOID.None;

        internal static void Reset()
        {
            resetRunning = false;
            committedStone = ZDOID.None;
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Start))]
        internal static class RegisterPatch
        {
            [HarmonyPostfix]
            private static void Register()
            {
                if (ZRoutedRpc.instance == null) { return; }

                ZRoutedRpc.instance.Register<ZDOID, int, long, string>(AskRpc, Serve);
                ZRoutedRpc.instance.Register<ZDOID, int, float, string>(TellRpc, Hear);
                RenewalEffects.Register(ZRoutedRpc.instance);

                if (ZNet.instance != null && ZNet.instance.IsServer()) { CharacterResetLog.Load(); }
            }
        }

        internal static void Ask(ZDOID stone, int mode)
        {
            if (ZRoutedRpc.instance == null || stone == ZDOID.None) { return; }

            Player player = Player.m_localPlayer;
            long id = player != null ? player.GetPlayerID() : 0L;
            string name = player != null ? player.GetPlayerName() : string.Empty;

            ZRoutedRpc.instance.InvokeRoutedRPC(AskRpc, stone, mode, id, name);
        }

        private static void Hear(long sender, ZDOID stone, int state, float seconds, string text)
        {
            BossStoneReset.Answered(stone, state, seconds, text);
        }

        private static void Serve(long sender, ZDOID stone, int mode, long playerId, string playerName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return; }
            if (ZDOMan.instance == null) { return; }

            ZDO zdo = ZDOMan.instance.GetZDO(stone);
            if (zdo == null || !zdo.IsValid() || !BossStone.IsOurs(zdo.GetPrefab()))
            {
                return;
            }

            Vector3 stonePos = zdo.GetPosition();
            string location = BossStonePlacement.LocationNameOf(zdo, stonePos);
            if (string.IsNullOrEmpty(location) || !StarLevelSystem.API.SupportsLocationReset)
            {
                return;
            }

            Vector3 center = BossStonePlacement.LocationCenterOf(zdo, stonePos);

            if (mode != ModeStatus && !WithinReach(sender, stonePos))
            {
                Tell(sender, stone, StateFailed, 0f, "Too far from the stone");
                return;
            }

            if (mode == ModeStatus && Fresh(zdo)) { return; }

            StarLevelSystem.API.GetLocationResetTargetInfo(location, target =>
            {
                float manual = Remaining(location, center, target);
                double scheduled = ScheduledSeconds(location, center);

                Stamp(zdo, manual > 0f ? Now() + (long)manual : 0L, scheduled);

                if (mode == ModeStatus) { return; }

                if (mode == ModeScheduled)
                {
                    switch (ScheduledGate(scheduled, center))
                    {
                        case Gate.TrophyOnly:
                            Tell(sender, stone, StateFailed, 0f, "Trophy only");
                            return;

                        case Gate.NotDue:
                            Tell(sender, stone, StateFailed, 0f, "Not due yet");
                            return;

                        case Gate.NoSchedule:
                            return;

                        case Gate.Busy:
                            Tell(sender, stone, StateBusy, 0f, string.Empty);
                            return;

                        case Gate.Occupied:
                            Tell(sender, stone, StateFailed, 0f, "Someone is inside");
                            return;
                    }

                    if (zdo.GetInt(ZDOVars.s_item) != 0)
                    {
                        DropTrophy(zdo, $"the scheduled claim on '{location}'");
                    }

                    Renew(sender, stone, stonePos, location, center, playerId, playerName, spendsTrophyClock: false);
                    return;
                }

                if (manual > 0f)
                {
                    Tell(sender, stone, StateCooling, manual, string.Empty);
                    return;
                }

                if (zdo.GetInt(ZDOVars.s_item) == 0)
                {
                    Tell(sender, stone, StateFailed, 0f, "Needs a trophy");
                    return;
                }

                if (resetRunning)
                {
                    Tell(sender, stone, StateBusy, 0f, string.Empty);
                    return;
                }

                if (SomeoneInside(center))
                {
                    Tell(sender, stone, StateFailed, 0f, "Someone is inside");
                    return;
                }

                committedStone = stone;
                Renew(sender, stone, stonePos, location, center, playerId, playerName, spendsTrophyClock: true);
            });
        }

        private enum Gate { Open, TrophyOnly, NotDue, NoSchedule, Busy, Occupied }

        private static Gate ScheduledGate(double scheduled, Vector3 center)
        {
            if (!VRNConfig.AllowForceScheduledReset.Value) { return Gate.TrophyOnly; }
            if (scheduled > 0d) { return Gate.NotDue; }
            if (scheduled != 0d) { return Gate.NoSchedule; }
            if (resetRunning) { return Gate.Busy; }
            if (SomeoneInside(center)) { return Gate.Occupied; }
            return Gate.Open;
        }
        private static bool DropTrophy(ZDO zdo, string why)
        {
            ZNetView view;
            if (ZNetScene.instance == null ||
                !ZNetScene.instance.m_instances.TryGetValue(zdo, out view) || view == null)
            {
                return false;
            }

            ItemStand stand = view.GetComponentInChildren<ItemStand>(true);
            if (stand == null)
            {
                return false;
            }

            if (!view.IsOwner()) { view.ClaimOwnership(); }
            stand.DropItem();
            return true;
        }

        private static float Remaining(string location, Vector3 center, Dictionary<string, object> target)
        {
            string key = CharacterResetLog.KeyFor(location, center);

            if (VRNConfig.AlwaysAllowTrophyResets.Value)
            {
                return 0f;
            }

            double interval = Number(target, "resetSeconds", 0d);

            if (interval <= 0d)
            {
                return 0f;
            }

            long last = CharacterResetLog.LastReset(key);
            if (last <= 0L)
            {
                return 0f;
            }

            double left = last + interval - Now();

            return left > 0d ? (float)left : 0f;
        }

        internal const string ReadyAtKey = "VRN_readyAt";
        internal const string SchedAtKey = "VRN_schedAt";

        internal const string StampedAtKey = "VRN_stampedAt";

        internal static bool Fresh(ZDO zdo)
        {
            float interval = VRNConfig.ReadIntervalSeconds;
            if (interval <= 0f) { return false; }

            long stamped = zdo.GetLong(StampedAtKey, 0L);
            if (stamped <= 0L) { return false; }

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamped < (long)interval;
        }

        private static void Stamp(ZDO zdo, long readyAt, double scheduled)
        {
            if (!zdo.IsOwner())
            {
                ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                zdo.SetOwner(ZDOMan.GetSessionID());
            }

            zdo.Set(ReadyAtKey, readyAt);
            zdo.Set(SchedAtKey, scheduled >= 0d ? Now() + (long)scheduled : -1L);
            zdo.Set(StampedAtKey, Now());
        }

        private static void Restamp(ZDOID stone, string location, Vector3 center)
        {
            if (ZDOMan.instance == null) { return; }

            ZDO zdo = ZDOMan.instance.GetZDO(stone);

            if (zdo == null || !zdo.IsValid()) { return; }

            StarLevelSystem.API.GetLocationResetTargetInfo(location, target =>
            {
                float manual = Remaining(location, center, target);
                double scheduled = ScheduledSeconds(location, center);

                Stamp(zdo, manual > 0f ? Now() + (long)manual : 0L, scheduled);
            });
        }

        private static double ScheduledSeconds(string location, Vector3 center)
        {
            double answer = -1d;

            StarLevelSystem.API.GetLocationResetInfo(
                location, center, VRNConfig.ResetSearchRadius,
                info =>
                {
                    if (!Flag(info, "found")) { return; }
                    if (!Flag(info, "configured") || !Flag(info, "enabled")) { return; }

                    if (Number(info, "lastResetUnix", 0d) <= 0d)
                    {
                        answer = 0d;
                        return;
                    }

                    answer = Number(info, "secondsUntilDue", -1d);
                });

            return answer;
        }

        private static string Stamp(long unix)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "Z";
        }

        private static void Renew(long sender, ZDOID stone, Vector3 stonePos, string location, Vector3 center,
                                  long playerId, string playerName, bool spendsTrophyClock)
        {
            resetRunning = true;
            committedStone = stone;

            float delay = VRNConfig.ResetDelaySeconds;

            RenewalEffects.Seal(center, delay + VRNConfig.SealSeconds);

            if (delay <= 0f || ValheimResetNow.Instance == null)
            {
                Fire(sender, stone, stonePos, location, center, playerId, playerName, spendsTrophyClock);
                return;
            }

            ValheimResetNow.Instance.StartCoroutine(
                Countdown(delay, sender, stone, stonePos, location, center, playerId, playerName,
                         spendsTrophyClock));
        }

        private static IEnumerator Countdown(float delay, long sender, ZDOID stone, Vector3 stonePos,
                                             string location, Vector3 center, long playerId, string playerName,
                                             bool spendsTrophyClock)
        {
            yield return new WaitForSeconds(delay);

            if (ZNet.instance == null || !ZNet.instance.IsServer() || !StarLevelSystem.API.SupportsLocationReset)
            {
                resetRunning = false;
                committedStone = ZDOID.None;
                RenewalEffects.Seal(center, 0f);
                yield break;
            }

            Fire(sender, stone, stonePos, location, center, playerId, playerName, spendsTrophyClock);
        }

        private static void Fire(long sender, ZDOID stone, Vector3 stonePos, string location, Vector3 center,
                                 long playerId, string playerName, bool spendsTrophyClock)
        {
            StarLevelSystem.API.ResetNamedLocation(location, center, VRNConfig.ResetSearchRadius,
                safety: 1,
                includeDetail: true,
                onComplete: result =>
                {
                    resetRunning = false;
                    committedStone = ZDOID.None;
                    RenewalEffects.Seal(center, 0f);

                    bool rebuilt = Flag(result, "completed") && Count(result, "locationsRebuilt") > 0;

                    if (!rebuilt)
                    {
                        if (Count(result, "zonesBlocked") == 1)
                        {
                            string what;
                            Vector3 where;
                            if (ParseBlocker(Blockers(result), out what, out where))
                            {
                                RenewalEffects.Blame(stonePos, where, what, sender);
                            }
                        }

                        Tell(sender, stone, StateFailed, 0f, Excuse(result));
                        return;
                    }

                    if (spendsTrophyClock)
                    {
                        CharacterResetLog.Record(playerId, playerName, location, center, Now());
                    }

                    RenewalEffects.Announce(sender, stonePos);

                    Restamp(stone, location, center);

                    Tell(sender, stone, StateDone, 0f, string.Empty);
                });
        }

        private static string Excuse(Dictionary<string, object> result)
        {
            switch (Text(result, "refusalCode"))
            {
                case "cooldown":
                case "already_running":
                case "not_ready":
                    return "Still settling";
                case "no_such_location":
                    return "Chamber not found";
                case "":
                    return Refusal(result);
                default:
                    return "Renewal refused";
            }
        }

        private static void Tell(long peer, ZDOID stone, int state, float seconds, string text)
        {
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, TellRpc, stone, state, seconds, text ?? string.Empty);
        }

        private static bool SomeoneInside(Vector3 center)
        {
            Vector3 interior = center + Vector3.up * InteriorHeight;
            float radius = VRNConfig.ResetSearchRadius;

            ZNet net = ZNet.instance;
            if (net != null)
            {
                List<ZNetPeer> peers = net.GetPeers();
                for (int i = 0; i < peers.Count; i++)
                {
                    if (Inside(peers[i].m_refPos, interior, radius)) { return true; }
                }
            }

            Player local = Player.m_localPlayer;
            return local != null && Inside(local.transform.position, interior, radius);
        }

        private const float InteriorHeight = 5000f;
        private const float InteriorFloor = 3000f;

        private static bool Inside(Vector3 point, Vector3 interior, float radius)
        {
            if (point.y < InteriorFloor) { return false; }

            Vector3 delta = point - interior;
            delta.y = 0f;
            return delta.sqrMagnitude <= radius * radius;
        }

        private static bool WithinReach(long sender, Vector3 stonePos)
        {
            float reach = VRNConfig.ResetSearchRadius;

            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            Vector3 from = peer != null ? peer.m_refPos
                                        : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position
                                                                        : stonePos);
            return (from - stonePos).sqrMagnitude <= reach * reach;
        }

        private static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static string Refusal(Dictionary<string, object> result)
        {
            int blocked = Count(result, "zonesBlocked");
            string list = blocked > 0 ? Blockers(result) : string.Empty;

            string one;
            Vector3 ignored;

            switch (blocked)
            {
                case 0:
                    string reason = Text(result, "reason");
                    return reason.Length > 0 ? reason : "Nothing needed resetting";

                case 1 when ParseBlocker(list, out one, out ignored) && one.Length > 0:
                    return "Blocked by " + one;

                case 1 when list.Length == 0:
                    return "Blocked by 1 chunk";

                default:
                    return list.Length > 0
                        ? "Blocked by\n" + list
                        : $"Blocked by {blocked} chunks";
            }
        }

        private static string Blockers(Dictionary<string, object> result)
        {
            object raw;
            if (result == null || !result.TryGetValue("zones", out raw)) { return string.Empty; }

            System.Collections.IEnumerable zones = raw as System.Collections.IEnumerable;
            if (zones == null) { return string.Empty; }

            List<string> reasons = new List<string>();

            try
            {
                foreach (object entry in zones)
                {
                    System.Collections.IDictionary zone = entry as System.Collections.IDictionary;
                    if (zone == null) { continue; }

                    if (!zone.Contains("skipReason")) { continue; }

                    object text = zone["skipReason"];
                    if (text == null) { continue; }

                    string reason = text.ToString().Trim();
                    if (reason.Length == 0 || reasons.Contains(reason)) { continue; }

                    reasons.Add(reason);
                    if (reasons.Count == MaxBlockersShown) { break; }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }

            return string.Join("\n", reasons.ToArray());
        }

        private const int MaxBlockersShown = 10;

        private static bool ParseBlocker(string reason, out string what, out Vector3 where)
        {
            what = string.Empty;
            where = Vector3.zero;
            if (string.IsNullOrEmpty(reason)) { return false; }

            int open = reason.IndexOf('\'');
            int close = open >= 0 ? reason.IndexOf('\'', open + 1) : -1;
            if (open >= 0 && close > open) { what = reason.Substring(open + 1, close - open - 1); }

            float x, z;
            if (Coord(reason, "x=", out x) && Coord(reason, "z=", out z))
            {
                where = new Vector3(x, 0f, z);
            }

            return what.Length > 0 || where != Vector3.zero;
        }

        private static bool Coord(string text, string tag, out float value)
        {
            value = 0f;

            int at = text.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) { return false; }

            int start = at + tag.Length;
            int end = start;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '-' || text[end] == '.')) { end++; }
            if (end == start) { return false; }

            return float.TryParse(text.Substring(start, end - start), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out value);
        }

        private static bool Flag(Dictionary<string, object> result, string key)
        {
            object value;
            if (result == null || !result.TryGetValue(key, out value) || value == null) { return false; }
            try { return Convert.ToBoolean(value); }
            catch (Exception) { return false; }
        }

        private static int Count(Dictionary<string, object> result, string key)
        {
            return (int)Number(result, key, 0d);
        }

        private static double Number(Dictionary<string, object> result, string key, double fallback)
        {
            object value;
            if (result == null || !result.TryGetValue(key, out value) || value == null) { return fallback; }
            try { return Convert.ToDouble(value); }
            catch (Exception) { return fallback; }
        }

        private static string Text(Dictionary<string, object> result, string key)
        {
            object value;
            if (result == null || !result.TryGetValue(key, out value) || value == null) { return string.Empty; }
            return value.ToString();
        }
    }
}
