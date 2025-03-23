/*
 * Copyright (C) 2024 Game4Freak.io
 * This mod is provided under the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using CompanionServer.Handlers;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Rust;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Safe Respawn", "VisEntities", "1.3.0")]
    [Description("Gives players temporary protection after spawning.")]
    public class SafeRespawn : RustPlugin
    {
        #region Fields

        private static SafeRespawn _plugin;
        private static Configuration _config;
        private StoredData _storedData;
        private Dictionary<ulong, DateTime> _playerProtectionEndTimes = new Dictionary<ulong, DateTime>();
        private const int LAYER_BEDS = Layers.Mask.Deployed;

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Protection Duration Seconds")]
            public float ProtectionDurationSeconds { get; set; }

            [JsonProperty("Enable Protection Against NPC")]
            public bool EnableProtectionAgainstNPC { get; set; }

            [JsonProperty("Enable Protection Against Animals")]
            public bool EnableProtectionAgainstAnimals { get; set; }

            [JsonProperty("Enable Protection Against Patrol Helicopter")]
            public bool EnableProtectionAgainstHelicopter { get; set; }

            [JsonProperty("Protected Players Cannot Harm Others")]
            public bool ProtectedPlayersCannotHarmOthers { get; set; }

            [JsonProperty("Protect Owned Entities")]
            public bool ProtectOwnedEntities { get; set; }

            [JsonProperty("Enable Protection Only For First Spawn")]
            public bool EnableProtectionOnlyForFirstSpawn { get; set; }

            [JsonProperty("Ignore Sleeping Bag Spawns")]
            public bool IgnoreSleepingBagSpawns { get; set; }

            [JsonProperty("Reset Data On Wipe")]
            public bool ResetDataOnWipe { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.1.0") < 0)
            {
                _config.ResetDataOnWipe = defaultConfig.ResetDataOnWipe;
            }

            if (string.Compare(_config.Version, "1.2.0") < 0)
            {
                _config.EnableProtectionAgainstAnimals = defaultConfig.EnableProtectionAgainstAnimals;
            }

            if (string.Compare(_config.Version, "1.3.0") < 0)
            {
                _config.EnableProtectionAgainstHelicopter= defaultConfig.EnableProtectionAgainstHelicopter;
                _config.ProtectedPlayersCannotHarmOthers= defaultConfig.ProtectedPlayersCannotHarmOthers;
                _config.ProtectOwnedEntities = defaultConfig.ProtectOwnedEntities;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                ProtectionDurationSeconds = 60f,
                EnableProtectionOnlyForFirstSpawn = true,
                EnableProtectionAgainstAnimals = true,
                EnableProtectionAgainstNPC = true,
                EnableProtectionAgainstHelicopter = true,
                ProtectedPlayersCannotHarmOthers = true,
                ProtectOwnedEntities = true,
                IgnoreSleepingBagSpawns = true,
                ResetDataOnWipe = true
            };
        }

        #endregion Configuration

        #region Stored Data

        public class StoredData
        {
            [JsonProperty("Previously Connected Players")]
            public HashSet<ulong> PreviouslyConnectedPlayers { get; set; } = new HashSet<ulong>();
        }

        #endregion Stored Data

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
            PermissionUtil.RegisterPermissions();
            _storedData = DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
        }

        private void Unload()
        {
            _config = null;
            _plugin = null;
        }

        private void OnNewSave()
        {
            if (_config.ResetDataOnWipe)
                DataFileUtil.LoadOrCreate<StoredData>(DataFileUtil.GetFilePath());
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !PermissionUtil.HasPermission(player, PermissionUtil.USE))
                return;

            bool isFirstSpawn = !_storedData.PreviouslyConnectedPlayers.Contains(player.userID);
            bool applyProtection = !_config.EnableProtectionOnlyForFirstSpawn || isFirstSpawn;

            if (applyProtection)
            {
                NextTick(() =>
                {
                    if (!_playerProtectionEndTimes.TryGetValue(player.userID, out DateTime protectionEndTime) || DateTime.Now >= protectionEndTime)
                    {
                        if (!_config.IgnoreSleepingBagSpawns || !AnySleepingBagOrBedNearby(player.transform.position, 2f))
                        {
                            _playerProtectionEndTimes[player.userID] = DateTime.Now.AddSeconds(_config.ProtectionDurationSeconds);
                        }
                    }

                    if (isFirstSpawn)
                    {
                        _storedData.PreviouslyConnectedPlayers.Add(player.userID);
                        DataFileUtil.Save(DataFileUtil.GetFilePath(), _storedData);
                    }
                });
            }
        }

        private object OnEntityTakeDamage(BaseEntity hurtEntity, HitInfo hitInfo)
        {
            if (hurtEntity == null || hitInfo == null)
                return null;

            if (hurtEntity is BasePlayer victimPlayer)
            {
                return HandleDamageToPlayer(victimPlayer, hitInfo);
            }
            else if (hurtEntity is BaseEntity entity)
            {
                return HandleDamageToOwnedEntity(hurtEntity, hitInfo);
            }

            return null;
        }

        #endregion Oxide Hooks

        #region Damage Handling

        private object HandleDamageToPlayer(BasePlayer victim, HitInfo hitInfo)
        {
            if (victim == null || hitInfo == null)
                return null;

            BasePlayer attackerPlayer = hitInfo.InitiatorPlayer;
            BaseNpc animalAttacker = hitInfo.Initiator as BaseNpc;
            PatrolHelicopter heliAttacker = hitInfo.Initiator as PatrolHelicopter;

            if (_playerProtectionEndTimes.TryGetValue(victim.userID, out DateTime victimProtectionEnd))
            {
                if (DateTime.Now < victimProtectionEnd)
                {
                    if (!_config.EnableProtectionAgainstNPC && attackerPlayer != null && attackerPlayer.IsNpc)
                        return null;

                    if (!_config.EnableProtectionAgainstAnimals && animalAttacker != null)
                        return null;

                    if (!_config.EnableProtectionAgainstHelicopter && heliAttacker != null)
                        return null;

                    if (attackerPlayer != null && !IsNPC(attackerPlayer))
                    {
                        TimeSpan remaining = victimProtectionEnd - DateTime.Now;
                        MessagePlayer(attackerPlayer, Lang.PlayerProtected, FormatTime(remaining));
                    }

                    hitInfo.damageTypes.Clear();
                    return true;
                }
                else
                {
                    _playerProtectionEndTimes.Remove(victim.userID);
                }
            }

            if (attackerPlayer != null
                && _config.ProtectedPlayersCannotHarmOthers
                && _playerProtectionEndTimes.TryGetValue(attackerPlayer.userID, out DateTime attackerProtectionEnd)
                && DateTime.Now < attackerProtectionEnd)
            {
                TimeSpan remaining = attackerProtectionEnd - DateTime.Now;
                MessagePlayer(attackerPlayer, Lang.ProtectedCantAttackOthers, FormatTime(remaining));

                hitInfo.damageTypes.Clear();
                return true;
            }

            return null;
        }

        private object HandleDamageToOwnedEntity(BaseEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null)
                return null;

            if (!_config.ProtectOwnedEntities)
                return null;

            BasePlayer owner = FindPlayerById(entity.OwnerID);
            if (owner == null || IsNPC(owner))
                return null;

            BasePlayer attackerPlayer = hitInfo.InitiatorPlayer;
            if (attackerPlayer == null || IsNPC(attackerPlayer))
                return null;

            if (_playerProtectionEndTimes.TryGetValue(owner.userID, out DateTime protectionEndTime))
            {
                if (DateTime.Now < protectionEndTime)
                {
                    TimeSpan remaining = protectionEndTime - DateTime.Now;
                    MessagePlayer(attackerPlayer, Lang.OwnedEntityProtected, FormatTime(remaining));

                    hitInfo.damageTypes.Clear();
                    return true;
                }
                else
                {
                    _playerProtectionEndTimes.Remove(owner.userID);
                }
            }

            return null;
        }

        #endregion Damage Handling

        #region Sleeping Bag Detection

        private bool AnySleepingBagOrBedNearby(Vector3 position, float radius)
        {
            List<SleepingBag> nearbySleepingBags = Pool.Get<List<SleepingBag>>();
            bool isNearSleepingBagOrBed = false;

            Vis.Entities(position, radius, nearbySleepingBags, LAYER_BEDS, QueryTriggerInteraction.Ignore);

            foreach (SleepingBag sleepingBag in nearbySleepingBags)
            {
                if (sleepingBag != null)
                {
                    isNearSleepingBagOrBed = true;
                    break;
                }
            }

            Pool.FreeUnmanaged(ref nearbySleepingBags);
            return isNearSleepingBagOrBed;
        }

        #endregion Sleeping Bag Detection

        #region Helper Functions

        public static BasePlayer FindPlayerById(ulong playerId)
        {
            return RelationshipManager.FindByID(playerId);
        }

        public static bool IsNPC(BasePlayer player)
        {
            return player.IsNpc || !player.userID.IsSteamId();
        }

        private string FormatTime(TimeSpan timeSpan)
        {
            if (timeSpan.Days > 0)
                return $"{timeSpan.Days}d {timeSpan.Hours}h";

            if (timeSpan.Hours > 0)
                return $"{timeSpan.Hours}h {timeSpan.Minutes}m";

            if (timeSpan.Minutes > 0)
                return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";

            return $"{timeSpan.Seconds}s";
        }

        #endregion Helper Functions

        #region Helper Classes

        public class DataFileUtil
        {
            private const string FOLDER = "";

            public static void EnsureFolderCreated()
            {
                string path = Path.Combine(Interface.Oxide.DataDirectory, FOLDER);

                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }

            public static string GetFilePath(string filename = null)
            {
                if (filename == null)
                    filename = _plugin.Name;

                return Path.Combine(FOLDER, filename);
            }

            public static string[] GetAllFilePaths(bool filenameOnly = false)
            {
                string[] filePaths = Interface.Oxide.DataFileSystem.GetFiles(FOLDER);

                for (int i = 0; i < filePaths.Length; i++)
                {
                    filePaths[i] = filePaths[i].Substring(0, filePaths[i].Length - 5);

                    if (filenameOnly)
                    {
                        filePaths[i] = Path.GetFileName(filePaths[i]);
                    }
                }
                return filePaths;
            }

            public static bool Exists(string filePath)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(filePath);
            }

            public static T Load<T>(string filePath) where T : class, new()
            {
                T data = Interface.Oxide.DataFileSystem.ReadObject<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static T LoadIfExists<T>(string filePath) where T : class, new()
            {
                if (Exists(filePath))
                    return Load<T>(filePath);
                else
                    return null;
            }

            public static T LoadOrCreate<T>(string filePath) where T : class, new()
            {
                T data = LoadIfExists<T>(filePath);
                if (data == null)
                    data = new T();

                return data;
            }

            public static void Save<T>(string filePath, T data)
            {
                Interface.Oxide.DataFileSystem.WriteObject<T>(filePath, data);
            }

            public static void Delete(string filePath)
            {
                Interface.Oxide.DataFileSystem.DeleteDataFile(filePath);
            }
        }

        #endregion Helper Classes

        #region Permissions

        private static class PermissionUtil
        {
            public const string USE = "saferespawn.use";
            private static readonly List<string> _permissions = new List<string>
            {
                USE,
            };

            public static void RegisterPermissions()
            {
                foreach (var permission in _permissions)
                {
                    _plugin.permission.RegisterPermission(permission, _plugin);
                }
            }

            public static bool HasPermission(BasePlayer player, string permissionName)
            {
                return _plugin.permission.UserHasPermission(player.UserIDString, permissionName);
            }
        }

        #endregion Permissions

        #region Localization

        private class Lang
        {
            public const string PlayerProtected = "PlayerProtected ";
            public const string OwnedEntityProtected = "OwnedEntityProtected";
            public const string ProtectedCantAttackOthers = "ProtectedCantAttackOthers";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.PlayerProtected] = "The player you tried to attack is under spawn protection for {0}.",
                [Lang.OwnedEntityProtected] = "You cannot damage this player's structures; they are spawn protected for {0}.",
                [Lang.ProtectedCantAttackOthers] = "You cannot attack players while you are under spawn protection for {0}."
            }, this, "en");
        }

        private static string GetMessage(BasePlayer player, string messageKey, params object[] args)
        {
            string message = _plugin.lang.GetMessage(messageKey, _plugin, player.UserIDString);

            if (args.Length > 0)
                message = string.Format(message, args);

            return message;
        }

        public static void MessagePlayer(BasePlayer player, string messageKey, params object[] args)
        {
            string message = GetMessage(player, messageKey, args);
            _plugin.SendReply(player, message);
        }

        #endregion Localization
    }
}