using System.Collections;
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimResetNow
{
    internal static class BossStonePlacement
    {
        private const string TargetLocations =
            "Crypt2, Crypt3, Crypt4, Hildir_crypt, SunkenCrypt4, Hildir_cave, MountainCave02, " +
            "Hildir_plainsfortress, Mistlands_DvergrTownEntrance1, Mistlands_DvergrTownEntrance2, " +
            "CharredFortress";

        internal class Spot
        {
            internal readonly float Angle;
            internal readonly float Distance;
            internal readonly float Side;
            internal readonly float Height;
            internal readonly float Yaw;

            internal Spot(float angle, float distance, float side, float height, float yaw)
            {
                Angle = angle;
                Distance = distance;
                Side = side;
                Height = height;
                Yaw = yaw;
            }
        }

        private static readonly Dictionary<int, Spot> builtIn = new Dictionary<int, Spot>
        {
            { "Crypt2".GetStableHashCode(), new Spot(146.25f, 3.8f, 4.7f, 0f, -31.15f) },
            { "Crypt3".GetStableHashCode(), new Spot(146.25f, 4.8f, 7.7f, 0f, -51.15f) },
            { "Crypt4".GetStableHashCode(), new Spot(146.25f, -10f, -9f, 0f, -90f) },
            { "Hildir_crypt".GetStableHashCode(), new Spot(146.25f, -10f, -9f, 0f, -90f) },

            { "SunkenCrypt4".GetStableHashCode(), new Spot(146.25f, 3.8f, 4.7f, 0f, -50f) },

            { "MountainCave02".GetStableHashCode(), new Spot(167f, -1.25f, -5f, -1.6f, -123f) },
            { "Hildir_cave".GetStableHashCode(), new Spot(167f, -1.25f, -5f, -1.6f, -123f) },

            { "Hildir_plainsfortress".GetStableHashCode(), new Spot(146.25f, 12f, 4.7f, 2.5f, -10f) },

            { "Mistlands_DvergrTownEntrance1".GetStableHashCode(), new Spot(170f, 1.2f, -3.2f, -1.2f, -110f) },
            { "Mistlands_DvergrTownEntrance2".GetStableHashCode(), new Spot(146.25f, -15f, -14.42f, 0.5f, -78) },

            { "CharredFortress".GetStableHashCode(), new Spot(235, 18f, -17f, 0f, -80.5f) }

        };

        private static readonly HashSet<int> targetHashes = BuildTargets();

        private static HashSet<int> BuildTargets()
        {
            HashSet<int> built = new HashSet<int>();

            foreach (string part in TargetLocations.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0) { continue; }
                built.Add(name.GetStableHashCode());
            }

            return built;
        }

        internal static bool IsTarget(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) { return false; }
            return targetHashes.Contains(locationName.GetStableHashCode());
        }

        private static string NameOf(ZoneSystem.ZoneLocation location)
        {
            if (!string.IsNullOrEmpty(location.m_prefabName)) { return location.m_prefabName; }
            try { return location.m_prefab.Name; }
            catch (Exception) { return null; }
        }

        internal static Spot SpotFor(string locationName)
        {
            Spot measured;
            return !string.IsNullOrEmpty(locationName)
                   && builtIn.TryGetValue(locationName.GetStableHashCode(), out measured)
                ? measured
                : null;
        }

        private static readonly List<ZDO> zdoBuffer = new List<ZDO>();
        private static readonly HashSet<ZoneSystem.SectorIndex> visitedScratch = new HashSet<ZoneSystem.SectorIndex>();

        private static int locationProxyHash;

        internal static void Reset()
        {
            locationProxyHash = 0;
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnLocation")]
        internal static class SpawnLocationPatch
        {
            [HarmonyPostfix]
            private static void Place(ZoneSystem.ZoneLocation location, Vector3 pos, Quaternion rot,
                                      ZoneSystem.SpawnMode mode)
            {
                if (mode != ZoneSystem.SpawnMode.Full && mode != ZoneSystem.SpawnMode.Ghost) { return; }
                if (!Allowed()) { return; }
                if (location == null) { return; }

                string name = NameOf(location);
                if (!IsTarget(name)) { return; }

                if (mode == ZoneSystem.SpawnMode.Ghost)
                {
                    BossStoneSweep.PlaceZdo(name, pos, rot, location.m_exteriorRadius);
                    return;
                }

                TryPlace(name, location.m_exteriorRadius, pos, rot, "spawn");
            }
        }

        private static bool Allowed()
        {
            if (!BossStone.Ready) { return false; }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) { return false; }
            return ZNetScene.instance != null && ZDOMan.instance != null;
        }

        private static bool TryPlace(string locationName, float exteriorRadius, Vector3 pos, Quaternion rot,
                                     string via)
        {
            Spot place = SpotFor(locationName);
            if (place == null)
            {
                return false;
            }

            float distance = place.Distance;
            Vector3 spot;
            Quaternion stoneRot;
            Resolve(pos, rot, place, out spot, out stoneRot);

            if (AlreadyPlaced(pos, spot, exteriorRadius, distance))
            {
                return false;
            }

            string trophy = BossStone.TrophyFor(locationName);
            if (string.IsNullOrEmpty(trophy))
            {
                return false;
            }

            GameObject prefab = BossStone.For(trophy);
            if (prefab == null)
            {
                return false;
            }

            GameObject placed = UnityEngine.Object.Instantiate(prefab, spot, stoneRot);
            if (placed == null)
            {
                return false;
            }

            StampLocation(placed, locationName, pos);
            return true;
        }

        private static void StampLocation(GameObject placed, string locationName, Vector3 locationPos)
        {
            ZNetView view = placed.GetComponent<ZNetView>();
            ZDO zdo = view != null ? view.GetZDO() : null;
            if (zdo == null)
            {
                return;
            }

            zdo.Set(LocationNameKey, locationName);
            zdo.Set(LocationPosKey, locationPos);
        }
        internal const string LocationNameKey = "VRN_location";
        internal const string LocationPosKey = "VRN_locationPos";

        private static void Resolve(Vector3 pos, Quaternion rot, Spot place,
                                    out Vector3 spot, out Quaternion stoneRot)
        {
            Vector3 dir = rot * Quaternion.Euler(0f, place.Angle, 0f) * Vector3.forward;
            dir.y = 0f;
            dir = dir.sqrMagnitude < 1e-4f ? Vector3.right : dir.normalized;

            Vector3 side = Vector3.Cross(Vector3.up, dir);

            spot = pos + dir * place.Distance + side * place.Side;

            spot.y = pos.y + place.Height;

            stoneRot = rot;

            Vector3 look = pos - spot;
            look.y = 0f;
            if (look.sqrMagnitude > 1e-4f) { stoneRot = Quaternion.LookRotation(look.normalized); }

            stoneRot *= Quaternion.Euler(0f, place.Yaw, 0f);
        }

        private static bool AlreadyPlaced(Vector3 locationPos, Vector3 spot, float exteriorRadius, float distance)
        {
            Vector2s locationZone = ZoneSystem.GetZone(locationPos);
            Vector2s spotZone = ZoneSystem.GetZone(spot);

            float range = Mathf.Max(exteriorRadius, Mathf.Abs(distance)) + 8f;
            if (ZoneHasStone(locationZone, locationPos, range)) { return true; }
            if (spotZone.x != locationZone.x || spotZone.y != locationZone.y)
            {
                if (ZoneHasStone(spotZone, locationPos, range)) { return true; }
            }
            return false;
        }

        private static bool ZoneHasStone(Vector2s zone, Vector3 locationPos, float range)
        {
            float rangeSqr = range * range;
            zdoBuffer.Clear();
            visitedScratch.Clear();
            ZDOMan.instance.FindObjects(zone, zdoBuffer, visitedScratch);

            bool found = false;
            for (int i = 0; i < zdoBuffer.Count; i++)
            {
                ZDO zdo = zdoBuffer[i];
                if (zdo == null || !zdo.IsValid()) { continue; }
                if (!BossStone.IsOurs(zdo.GetPrefab())) { continue; }
                if (Destroyed(zdo)) { continue; }

                Vector3 delta = zdo.GetPosition() - locationPos;
                delta.y = 0f;
                if (delta.sqrMagnitude > rangeSqr) { continue; }
                found = true;
                break;
            }
            zdoBuffer.Clear();
            return found;
        }

        private static bool Destroyed(ZDO zdo)
        {
            List<ZDOID> pending = ZDOMan.instance.m_destroySendList;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i] == zdo.m_uid) { return true; }
            }
            return false;
        }

        private static bool TryReadLocationProxy(Vector2s zone, Vector3 locationPos, out Quaternion rot,
                                                 out Vector3 proxyPos)
        {
            rot = Quaternion.identity;
            proxyPos = locationPos;
            if (locationProxyHash == 0)
            {
                ZoneSystem zones = ZoneSystem.instance;
                if (zones == null || zones.m_locationProxyPrefab == null) { return false; }
                locationProxyHash = zones.m_locationProxyPrefab.name.GetStableHashCode();
            }

            zdoBuffer.Clear();
            visitedScratch.Clear();
            ZDOMan.instance.FindObjects(zone, zdoBuffer, visitedScratch);

            bool found = false;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < zdoBuffer.Count; i++)
            {
                ZDO zdo = zdoBuffer[i];
                if (zdo == null || !zdo.IsValid()) { continue; }
                if (zdo.GetPrefab() != locationProxyHash) { continue; }

                float sqr = (zdo.GetPosition() - locationPos).sqrMagnitude;
                if (sqr >= bestSqr) { continue; }
                bestSqr = sqr;
                rot = zdo.GetRotation();
                proxyPos = zdo.GetPosition();
                found = true;
            }
            zdoBuffer.Clear();
            return found;
        }

        internal static string LocationNameOf(ZDO zdo, Vector3 stonePos)
        {
            string stored = zdo.GetString(LocationNameKey, string.Empty);
            if (!string.IsNullOrEmpty(stored)) { return stored; }

            return null;
        }

        internal static Vector3 LocationCenterOf(ZDO zdo, Vector3 stonePos)
        {
            Vector3 stored = zdo.GetVec3(LocationPosKey, Vector3.zero);
            return stored == Vector3.zero ? stonePos : stored;
        }

    internal static class BossStoneSweep
    {
        private const int PerTick = 25;
        private const float TickSeconds = 0.25f;

        private static bool ran;

        internal static void Reset() { ran = false; }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        internal static class KickoffPatch
        {
            [HarmonyPostfix]
            private static void Kick()
            {
                if (ran) { return; }
                ran = true;
                if (ValheimResetNow.Instance != null) { ValheimResetNow.Instance.StartCoroutine(Run()); }
            }
        }

        private static IEnumerator Run()
        {
            while (ZoneSystem.instance == null || ZDOMan.instance == null || !BossStone.Ready ||
                   ZNet.instance == null || !ZNet.instance.IsServer())
            {
                yield return new WaitForSeconds(2f);
            }

            yield return new WaitForSeconds(15f);

            ZoneSystem zones = ZoneSystem.instance;
            List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>> work =
                new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>();

            foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> pair in zones.m_locationInstances)
            {
                if (pair.Value.m_location == null) { continue; }
                if (!IsTarget(NameOf(pair.Value.m_location))) { continue; }
                work.Add(pair);
            }

            for (int i = 0; i < work.Count; ++i)
            {
                Place(work[i].Key, work[i].Value);

                if ((i + 1) % PerTick == 0) { yield return new WaitForSeconds(TickSeconds); }
            }
        }

        private static void Place(Vector2s zoneID, ZoneSystem.LocationInstance inst)
        {
            Quaternion rot;
            Vector3 origin;
            if (!TryReadLocationProxy(zoneID, inst.m_position, out rot, out origin)) { return; }

            PlaceZdo(NameOf(inst.m_location), origin, rot, inst.m_location.m_exteriorRadius);
        }

        internal static void PlaceZdo(string name, Vector3 origin, Quaternion rot, float exteriorRadius)
        {
            Spot place = SpotFor(name);
            if (place == null) { return; }

            Vector3 spot;
            Quaternion stoneRot;
            Resolve(origin, rot, place, out spot, out stoneRot);

            if (AlreadyPlaced(origin, spot, exteriorRadius, place.Distance))
            {
                return;
            }

            GameObject prefab = BossStone.For(BossStone.TrophyFor(name));
            if (prefab == null) { return; }

            ZNetView proto = prefab.GetComponent<ZNetView>();
            if (proto == null) { return; }

            int hash = prefab.name.GetStableHashCode();

            ZDO zdo = ZDOMan.instance.CreateNewZDO(spot, hash);
            if (zdo == null) { return; }

            zdo.Persistent = proto.m_persistent;
            zdo.Type = proto.m_type;
            zdo.Distant = proto.m_distant;
            zdo.SetPrefab(hash);
            zdo.SetPosition(spot);
            zdo.SetRotation(stoneRot);

            if (proto.m_syncInitialScale)
            {
                Vector3 scale = prefab.transform.localScale;
                if (Mathf.Approximately(scale.x, scale.y) && Mathf.Approximately(scale.x, scale.z))
                {
                    zdo.Set(ZDOVars.s_scaleScalarHash, scale.x);
                }
                else
                {
                    zdo.Set(ZDOVars.s_scaleHash, scale);
                }
            }

            zdo.Set(LocationNameKey, name);
            zdo.Set(LocationPosKey, origin);
        }
    }
}
}
