using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimResetNow
{
    internal static class CharacterResetLog
    {
        private const string FolderName = "ValheimResetNow";
        private const string FileName = "CharacterResetsLog.yaml";

        internal class Book
        {
            public Dictionary<string, Character> Players { get; set; } = new Dictionary<string, Character>();
        }

        internal class Character
        {
            public string Name { get; set; } = string.Empty;

            public Dictionary<string, Entry> Resets { get; set; } = new Dictionary<string, Entry>();
        }

        internal class Entry
        {
            public string Location { get; set; } = string.Empty;
            public int X { get; set; }
            public int Z { get; set; }
            public long At { get; set; }

            public string AtUtc { get; set; } = string.Empty;

            public int Count { get; set; }
        }

        private static Book book;

        private static readonly Dictionary<string, long> newestByLocation = new Dictionary<string, long>();

        private static bool dirty;

        internal static string KeyFor(string location, Vector3 pos)
        {
            return location + "@" + Mathf.RoundToInt(pos.x) + "," + Mathf.RoundToInt(pos.z);
        }

        internal static void Load()
        {
            if (book != null) { return; }

            book = new Book();
            newestByLocation.Clear();
            dirty = false;

            string path = FilePath();
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                IDeserializer reader = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                using (StreamReader stream = File.OpenText(path))
                {
                    book = reader.Deserialize<Book>(stream) ?? new Book();
                }
                if (book.Players == null) { book.Players = new Dictionary<string, Character>(); }

                Reindex();
            }
            catch (Exception)
            {
                book = new Book();
                newestByLocation.Clear();
                string wreck = path + ".broken";
                try { File.Copy(path, wreck, true); } catch (Exception) { }
            }
        }

        private static void Reindex()
        {
            newestByLocation.Clear();
            foreach (KeyValuePair<string, Character> player in book.Players)
            {
                if (player.Value == null || player.Value.Resets == null) { continue; }
                foreach (KeyValuePair<string, Entry> reset in player.Value.Resets)
                {
                    long known;
                    if (newestByLocation.TryGetValue(reset.Key, out known) && known >= reset.Value.At) { continue; }
                    newestByLocation[reset.Key] = reset.Value.At;
                }
            }
        }

        internal static long LastReset(string key)
        {
            Load();
            long when;
            return newestByLocation.TryGetValue(key, out when) ? when : 0L;
        }

        internal static void Record(long playerId, string playerName, string location, Vector3 pos, long whenUnix)
        {
            Load();

            string owner = playerId != 0L ? playerId.ToString() : "unknown";
            Character character;
            if (!book.Players.TryGetValue(owner, out character) || character == null)
            {
                character = new Character();
                book.Players[owner] = character;
            }
            if (!string.IsNullOrEmpty(playerName)) { character.Name = playerName; }
            if (character.Resets == null) { character.Resets = new Dictionary<string, Entry>(); }

            string key = KeyFor(location, pos);
            Entry entry;
            if (!character.Resets.TryGetValue(key, out entry) || entry == null)
            {
                entry = new Entry { Location = location, X = Mathf.RoundToInt(pos.x), Z = Mathf.RoundToInt(pos.z) };
                character.Resets[key] = entry;
            }
            entry.At = whenUnix;
            entry.AtUtc = DateTimeOffset.FromUnixTimeSeconds(whenUnix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            entry.Count++;

            long known;
            if (!newestByLocation.TryGetValue(key, out known) || known < whenUnix)
            {
                newestByLocation[key] = whenUnix;
            }

            dirty = true;

            Flush();
        }

        internal static void Flush()
        {
            if (book == null || !dirty) { return; }

            string path = FilePath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                ISerializer writer = new SerializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                string text = writer.Serialize(book);

                string temp = path + ".tmp";
                File.WriteAllText(temp, text);
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(temp, path);

                dirty = false;
            }
            catch (Exception)
            {
            }
        }

        internal static void Unload()
        {
            Flush();
            book = null;
            newestByLocation.Clear();
            dirty = false;
        }

        private static string FilePath()
        {
            return Path.Combine(Path.Combine(Paths.ConfigPath, FolderName), FileName);
        }
    }
}
