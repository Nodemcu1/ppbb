using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PaintballUltra", "Copilot", "1.0.0")]
    [Description("Comprehensive paintball minigame system with premium UI and progression")]
    public class PaintballUltra : RustPlugin
    {
        #region Dependencies
        [PluginReference] private Plugin ImageLibrary;
        [PluginReference] private Plugin ZoneManager;
        #endregion

        #region Constants
        private const string DataFileName = "PaintballUltra_Data";
        private const string MainUi = "PBUI.Main";
        private const string LobbyUi = "PBUI.Lobby";
        private const string VotingUi = "PBUI.Voting";
        private const string HudUi = "PBUI.Hud";
        private const string ShopUi = "PBUI.Shop";
        private const string KillFeedUi = "PBUI.KillFeed";
        private const string WandItemShortname = "hammer";
        private const string DefaultSuit = "paintballoveralls.suit";
        private const string GunShortname = "paintballgun";
        private const string AmmoShortname = "ammo.paintball";
        private const string FlagShortname = "twitchrivalsflag";
        private const float SpawnProtectionSeconds = 3f;
        private const float SpawnProtectionRadius = 1.5f;
        private const float FlagInteractionDistance = 2f;
        private const float WandMaxRaycastDistance = 100f;
        private const float InstantKillDamageBuffer = 100f;
        private const string TeamAColor = "0.55 0.8 0.65 1";
        private const string TeamBColor = "0.82 0.5 0.3 1";
        private const string DarkPanelColor = "0.09 0.1 0.12 0.95";
        private const string SoftShadowColor = "0 0 0 0.35";
        private const string TextColor = "1 1 1 1";
        #endregion

        #region Data
        public static PaintballUltra Instance { get; private set; }
        private StoredData storedData;
        private readonly Dictionary<ulong, InventorySnapshot> storedInventories = new Dictionary<ulong, InventorySnapshot>();
        private readonly Dictionary<ulong, float> spawnProtection = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, PlayerSession> sessions = new Dictionary<ulong, PlayerSession>();
        private readonly HashSet<ulong> readyPlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, WandMode> wandModes = new Dictionary<ulong, WandMode>();
        private readonly List<string> voteOrder = new List<string>();
        private readonly Dictionary<string, int> voteCounts = new Dictionary<string, int>();
        private readonly Dictionary<ulong, string> voteSelections = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> killFeed = new Dictionary<ulong, string>();
        private readonly System.Random rng = new System.Random();
        private int savingData;
        private Timer phaseTimer;
        private Timer hudTimer;
        private Timer dataSaveTimer;
        private GamePhase currentPhase = GamePhase.Lobby;
        private GameMode currentMode = GameMode.TDM;
        private string activeArena;
        private int teamAScore;
        private int teamBScore;
        private ulong flagCarrierA;
        private ulong flagCarrierB;
        private Vector3? droppedFlagA;
        private Vector3? droppedFlagB;
        private BaseEntity droppedFlagEntityA;
        private BaseEntity droppedFlagEntityB;
        #endregion

        #region Configuration
        private PluginConfig config;

        private class PluginConfig
        {
            public int ScoreLimit = 25;
            public int WarmupSeconds = 10;
            public int VoteSeconds = 15;
            public int PostGameSeconds = 8;
            public int KillChips = 1;
            public int WinChips = 5;
            public int MaxVoteOptions = 3;
            public Dictionary<string, string> ImageUrls = new Dictionary<string, string>
            {
                ["background"] = "https://i.imgur.com/3u4d5uF.png",
                ["panel"] = "https://i.imgur.com/0s2vXvN.png",
                ["button"] = "https://i.imgur.com/7o8kYw2.png",
                ["accent"] = "https://i.imgur.com/4AiXzf8.png"
            };
            public List<CosmeticLook> CosmeticLooks = new List<CosmeticLook>
            {
                new CosmeticLook
                {
                    Name = "Urban Facemask",
                    Cost = 15,
                    Items = new List<string> { "mask.bandana", "hoodie", "pants", "boots.frog" }
                },
                new CosmeticLook
                {
                    Name = "Metro Defender",
                    Cost = 25,
                    Items = new List<string> { "mask.balaclava", "tshirt", "pants", "shoes.boots" }
                }
            };
        }

        protected override void LoadDefaultConfig() => config = new PluginConfig();
        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<PluginConfig>();
            if (config?.ImageUrls == null || config.CosmeticLooks == null)
            {
                LoadDefaultConfig();
            }
        }
        protected override void SaveConfig() => Config.WriteObject(config);
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            Instance = this;
            LoadData();
            AddCovalenceCommand("pb", "CommandOpen");
            AddCovalenceCommand("pbwand", "CommandWand");
            AddCovalenceCommand("pbarena", "CommandArena");
            AddCovalenceCommand("pbmode", "CommandMode");
        }

        private void OnServerInitialized()
        {
            RegisterImages();
            dataSaveTimer = timer.Every(60f, SaveDataAsync);
            if (storedData.Arenas.Count > 0 && string.IsNullOrEmpty(activeArena))
            {
                activeArena = storedData.Arenas.Keys.First();
            }
            SetPhase(GamePhase.Lobby);
        }

        private void Unload()
        {
            dataSaveTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyAllUi(player);
                if (sessions.ContainsKey(player.userID))
                {
                    LeaveGame(player);
                }
            }
            SaveDataAsync();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyAllUi(player);
            if (sessions.ContainsKey(player.userID))
            {
                LeaveGame(player);
            }
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (!sessions.ContainsKey(player.userID))
            {
                return;
            }
            if (currentMode == GameMode.Elimination && !sessions[player.userID].IsAlive)
            {
                player.inventory.Strip();
                TeleportToLobby(player);
                return;
            }
            if (currentPhase == GamePhase.Match || currentPhase == GamePhase.Warmup)
            {
                GiveLoadout(player);
                TeleportToSpawn(player);
                ApplySpawnProtection(player);
                UpdateHud(player);
            }
            else
            {
                TeleportToLobby(player);
            }
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity as BasePlayer;
            if (victim == null || !sessions.ContainsKey(victim.userID))
            {
                return;
            }
            if (currentPhase != GamePhase.Match && currentPhase != GamePhase.Warmup)
            {
                return;
            }
            if (IsSpawnProtected(victim))
            {
                info.damageTypes = new DamageTypeList();
                info.HitMaterial = 0;
                return;
            }
            var attacker = info.InitiatorPlayer;
            if (attacker != null && attacker.userID != victim.userID && sessions.ContainsKey(attacker.userID))
            {
                if (IsFriendlyFire(attacker, victim))
                {
                    info.damageTypes = new DamageTypeList();
                    info.HitMaterial = 0;
                    return;
                }
            }
            info.damageTypes = new DamageTypeList();
            info.damageTypes.Add(DamageType.Generic, victim.health + InstantKillDamageBuffer);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity as BasePlayer;
            if (victim == null || !sessions.ContainsKey(victim.userID))
            {
                return;
            }
            var attacker = info?.InitiatorPlayer;
            HandleKill(attacker, victim);
        }

        private void OnReloadWeapon(BasePlayer player, BaseProjectile projectile)
        {
            if (player == null || projectile == null || !sessions.ContainsKey(player.userID))
            {
                return;
            }
            projectile.primaryMagazine.contents = projectile.primaryMagazine.capacity;
            projectile.SendNetworkUpdateImmediate();
        }

        private void OnActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (player == null || !sessions.ContainsKey(player.userID))
            {
                return;
            }
            if (flagCarrierA == player.userID && (newItem == null || newItem.info.shortname != FlagShortname))
            {
                DropFlag(player, Team.TeamA);
            }
            if (flagCarrierB == player.userID && (newItem == null || newItem.info.shortname != FlagShortname))
            {
                DropFlag(player, Team.TeamB);
            }
        }

        private object OnItemDropped(Item item, BaseEntity entity)
        {
            if (item?.info?.shortname != FlagShortname)
            {
                return null;
            }
            var player = item.GetOwnerPlayer();
            if (player == null || !sessions.ContainsKey(player.userID))
            {
                return null;
            }
            if (flagCarrierA == player.userID)
            {
                flagCarrierA = 0;
                droppedFlagA = entity?.transform.position ?? player.transform.position;
                droppedFlagEntityA = entity;
            }
            if (flagCarrierB == player.userID)
            {
                flagCarrierB = 0;
                droppedFlagB = entity?.transform.position ?? player.transform.position;
                droppedFlagEntityB = entity;
            }
            return null;
        }
        #endregion

        #region Commands
        private void CommandOpen(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null)
            {
                return;
            }
            ShowMainUi(player);
        }

        private void CommandWand(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null || !player.IsAdmin)
            {
                iplayer.Reply("Admin only.");
                return;
            }
            if (args.Length == 0)
            {
                iplayer.Reply("Usage: /pbwand lobby|spawnA|spawnB|ffa|flagA|flagB");
                return;
            }
            if (!Enum.TryParse(args[0], true, out WandMode mode))
            {
                iplayer.Reply("Unknown mode.");
                return;
            }
            wandModes[player.userID] = mode;
            GiveWand(player);
            iplayer.Reply($"Wand set to {mode}.");
        }

        private void CommandArena(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null || !player.IsAdmin)
            {
                iplayer.Reply("Admin only.");
                return;
            }
            if (args.Length < 2)
            {
                iplayer.Reply("Usage: /pbarena create|select|delete <name>");
                return;
            }
            var action = args[0].ToLowerInvariant();
            var name = args[1];
            if (action == "create")
            {
                storedData.Arenas[name] = new Arena { Name = name };
                activeArena = name;
                SaveDataAsync();
                iplayer.Reply($"Arena {name} created.");
                return;
            }
            if (action == "select" && storedData.Arenas.ContainsKey(name))
            {
                activeArena = name;
                iplayer.Reply($"Arena {name} selected.");
                return;
            }
            if (action == "delete" && storedData.Arenas.Remove(name))
            {
                if (activeArena == name)
                {
                    activeArena = storedData.Arenas.Keys.FirstOrDefault();
                }
                SaveDataAsync();
                iplayer.Reply($"Arena {name} removed.");
                return;
            }
            iplayer.Reply("Arena action failed.");
        }

        private void CommandMode(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null || !player.IsAdmin)
            {
                iplayer.Reply("Admin only.");
                return;
            }
            if (args.Length == 0 || !Enum.TryParse(args[0], true, out GameMode mode))
            {
                iplayer.Reply("Usage: /pbmode tdm|ffa|elimination|ctf");
                return;
            }
            currentMode = mode;
            iplayer.Reply($"Game mode set to {mode}.");
        }
        #endregion

        #region UI Commands
        [ConsoleCommand("pbui.play")]
        private void ConsolePlay(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }
            JoinGame(player);
            ShowLobbyUi(player);
        }

        [ConsoleCommand("pbui.ready")]
        private void ConsoleReady(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }
            if (readyPlayers.Contains(player.userID))
            {
                readyPlayers.Remove(player.userID);
            }
            else
            {
                readyPlayers.Add(player.userID);
            }
            UpdateLobbyUi();
        }

        [ConsoleCommand("pbui.shop")]
        private void ConsoleShop(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }
            ShowShopUi(player);
        }

        [ConsoleCommand("pbui.buy")]
        private void ConsoleBuy(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }
            var lookName = string.Join(" ", arg.Args);
            PurchaseLook(player, lookName);
            ShowShopUi(player);
        }

        [ConsoleCommand("pbui.selectlook")]
        private void ConsoleSelectLook(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }
            var lookName = string.Join(" ", arg.Args);
            SelectLook(player, lookName);
            ShowShopUi(player);
        }

        [ConsoleCommand("pbui.vote")]
        private void ConsoleVote(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length == 0)
            {
                return;
            }
            var arenaName = string.Join(" ", arg.Args);
            HandleVote(player, arenaName);
            UpdateVotingUi();
        }
        #endregion

        #region Game Flow
        private void SetPhase(GamePhase phase)
        {
            currentPhase = phase;
            phaseTimer?.Destroy();
            if (phase == GamePhase.Lobby)
            {
                readyPlayers.Clear();
                StartLobby();
            }
            if (phase == GamePhase.Voting)
            {
                StartVoting();
            }
            if (phase == GamePhase.Warmup)
            {
                StartWarmup();
            }
            if (phase == GamePhase.Match)
            {
                StartMatch();
            }
            if (phase == GamePhase.PostGame)
            {
                StartPostGame();
            }
        }

        private void StartLobby()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (sessions.ContainsKey(player.userID))
                {
                    TeleportToLobby(player);
                    ShowLobbyUi(player);
                }
            }
            phaseTimer = timer.Every(2f, () =>
            {
                if (sessions.Count == 0)
                {
                    return;
                }
                if (readyPlayers.Count > 0 && readyPlayers.Count == sessions.Count)
                {
                    SetPhase(GamePhase.Voting);
                }
            });
        }

        private void StartVoting()
        {
            PrepareVoting();
            UpdateVotingUi();
            phaseTimer = timer.Once(config.VoteSeconds, () =>
            {
                DetermineVoteWinner();
                SetPhase(GamePhase.Warmup);
            });
        }

        private void StartWarmup()
        {
            teamAScore = 0;
            teamBScore = 0;
            AssignTeams();
            foreach (var player in sessions.Values.Select(x => x.Player))
            {
                player.Respawn();
            }
            phaseTimer = timer.Once(config.WarmupSeconds, () => SetPhase(GamePhase.Match));
        }

        private void StartMatch()
        {
            ResetFlags();
            foreach (var player in sessions.Values.Select(x => x.Player))
            {
                player.Respawn();
            }
            StartHudUpdates();
        }

        private void StartPostGame()
        {
            StopHudUpdates();
            foreach (var session in sessions.Values)
            {
                DestroyUi(session.Player, HudUi);
            }
            phaseTimer = timer.Once(config.PostGameSeconds, () => SetPhase(GamePhase.Lobby));
        }
        #endregion

        #region Core Gameplay
        private void JoinGame(BasePlayer player)
        {
            if (sessions.ContainsKey(player.userID))
            {
                return;
            }
            StoreInventory(player);
            sessions[player.userID] = new PlayerSession(player);
            readyPlayers.Remove(player.userID);
            player.Respawn();
            UpdateLobbyUi();
        }

        private void LeaveGame(BasePlayer player)
        {
            sessions.Remove(player.userID);
            readyPlayers.Remove(player.userID);
            RestoreInventory(player);
            UpdateLobbyUi();
        }

        private void AssignTeams()
        {
            var players = sessions.Values.Select(x => x.Player).ToList();
            var index = 0;
            foreach (var player in players)
            {
                var session = sessions[player.userID];
                if (currentMode == GameMode.FFA)
                {
                    session.Team = Team.None;
                }
                else
                {
                    session.Team = index % 2 == 0 ? Team.TeamA : Team.TeamB;
                    index++;
                }
                session.Score = 0;
                session.IsAlive = true;
            }
        }

        private void HandleKill(BasePlayer attacker, BasePlayer victim)
        {
            if (!sessions.TryGetValue(victim.userID, out var victimSession))
            {
                return;
            }
            victimSession.IsAlive = false;
            var victimData = GetPlayerData(victim.userID);
            victimData.Deaths++;

            if (currentPhase != GamePhase.Match && currentPhase != GamePhase.Warmup)
            {
                return;
            }

            if (attacker != null && sessions.TryGetValue(attacker.userID, out var attackerSession))
            {
                var data = GetPlayerData(attacker.userID);
                data.Kills++;
                data.PaintChips += config.KillChips;
                RefillAmmo(attacker);
                ShowKill(attacker.displayName, victim.displayName);

                if (currentMode == GameMode.TDM || currentMode == GameMode.CTF)
                {
                    if (attackerSession.Team == Team.TeamA)
                    {
                        teamAScore++;
                    }
                    else if (attackerSession.Team == Team.TeamB)
                    {
                        teamBScore++;
                    }
                    CheckMatchEnd();
                }
                if (currentMode == GameMode.FFA)
                {
                    attackerSession.Score++;
                    CheckMatchEnd();
                }
            }
            if (currentMode == GameMode.Elimination)
            {
                CheckEliminationEnd();
            }
        }

        private void CheckMatchEnd()
        {
            if (currentMode == GameMode.FFA)
            {
                var winner = sessions.Values.OrderByDescending(x => x.Score).FirstOrDefault();
                if (winner != null && winner.Score >= config.ScoreLimit)
                {
                    AwardWin(winner.Player);
                    SetPhase(GamePhase.PostGame);
                }
                return;
            }
            if (teamAScore >= config.ScoreLimit || teamBScore >= config.ScoreLimit)
            {
                var winningTeam = teamAScore >= config.ScoreLimit ? Team.TeamA : Team.TeamB;
                AwardWinTeam(winningTeam);
                SetPhase(GamePhase.PostGame);
            }
        }

        private void CheckEliminationEnd()
        {
            var aliveTeams = sessions.Values.Where(x => x.IsAlive).Select(x => x.Team).Distinct().ToList();
            if (aliveTeams.Count == 0)
            {
                return;
            }
            if (aliveTeams.Count == 1)
            {
                AwardWinTeam(aliveTeams[0]);
                SetPhase(GamePhase.PostGame);
            }
        }

        private void AwardWinTeam(Team team)
        {
            foreach (var session in sessions.Values.Where(x => x.Team == team))
            {
                AwardWin(session.Player);
            }
        }

        private void AwardWin(BasePlayer player)
        {
            var data = GetPlayerData(player.userID);
            data.Wins++;
            data.PaintChips += config.WinChips;
        }
        #endregion

        #region Inventory & Loadout
        private void GiveLoadout(BasePlayer player)
        {
            player.inventory.Strip();
            var gun = ItemManager.CreateByName(GunShortname, 1);
            if (gun != null)
            {
                gun.MoveToContainer(player.inventory.containerBelt);
            }
            var ammo = ItemManager.CreateByName(AmmoShortname, 128);
            if (ammo != null)
            {
                ammo.MoveToContainer(player.inventory.containerMain);
            }
            EquipLook(player);
            player.inventory.ServerUpdate(0f);
        }

        private void EquipLook(BasePlayer player)
        {
            var data = GetPlayerData(player.userID);
            var look = config.CosmeticLooks.FirstOrDefault(x => x.Name == data.ActiveLook);
            if (look == null || !data.UnlockedLooks.Contains(look.Name))
            {
                var suit = ItemManager.CreateByName(DefaultSuit, 1);
                suit?.MoveToContainer(player.inventory.containerWear);
                return;
            }
            foreach (var itemName in look.Items)
            {
                var item = ItemManager.CreateByName(itemName, 1);
                item?.MoveToContainer(player.inventory.containerWear);
            }
        }

        private void StoreInventory(BasePlayer player)
        {
            if (storedInventories.ContainsKey(player.userID))
            {
                return;
            }
            storedInventories[player.userID] = CaptureInventory(player);
        }

        private void RestoreInventory(BasePlayer player)
        {
            if (!storedInventories.TryGetValue(player.userID, out var snapshot))
            {
                return;
            }
            storedInventories.Remove(player.userID);
            player.inventory.Strip();
            RestoreContainer(player.inventory.containerMain, snapshot.Main);
            RestoreContainer(player.inventory.containerBelt, snapshot.Belt);
            RestoreContainer(player.inventory.containerWear, snapshot.Wear);
            snapshot.Metabolism?.Apply(player);
            player.inventory.ServerUpdate(0f);
        }

        private InventorySnapshot CaptureInventory(BasePlayer player)
        {
            return new InventorySnapshot
            {
                Main = CaptureContainer(player.inventory.containerMain),
                Belt = CaptureContainer(player.inventory.containerBelt),
                Wear = CaptureContainer(player.inventory.containerWear),
                Metabolism = MetabolismSnapshot.From(player)
            };
        }

        private List<ItemSnapshot> CaptureContainer(ItemContainer container)
        {
            if (container == null)
            {
                return new List<ItemSnapshot>();
            }
            return container.itemList.Select(item => ItemSnapshot.From(item)).ToList();
        }

        private void RestoreContainer(ItemContainer container, List<ItemSnapshot> snapshots)
        {
            if (container == null)
            {
                return;
            }
            foreach (var snapshot in snapshots)
            {
                snapshot.Restore(container);
            }
        }
        #endregion

        #region Movement & Spawning
        private void TeleportToLobby(BasePlayer player)
        {
            var arena = GetActiveArena();
            if (arena != null && arena.LobbyPosition != Vector3.zero)
            {
                player.Teleport(arena.LobbyPosition);
            }
        }

        private void TeleportToSpawn(BasePlayer player)
        {
            var arena = GetActiveArena();
            if (arena == null)
            {
                return;
            }
            var session = sessions[player.userID];
            var spawnList = arena.FFASpawns;
            if (currentMode != GameMode.FFA)
            {
                spawnList = session.Team == Team.TeamA ? arena.TeamASpawns : arena.TeamBSpawns;
            }
            if (spawnList.Count == 0)
            {
                TeleportToLobby(player);
                return;
            }
            var spawn = spawnList[rng.Next(spawnList.Count)];
            player.Teleport(spawn);
        }

        private void ApplySpawnProtection(BasePlayer player)
        {
            spawnProtection[player.userID] = Time.realtimeSinceStartup + SpawnProtectionSeconds;
            player.SendConsoleCommand("ddraw.sphere", SpawnProtectionSeconds, Color.cyan, player.transform.position, SpawnProtectionRadius);
        }

        private bool IsSpawnProtected(BasePlayer player)
        {
            if (!spawnProtection.TryGetValue(player.userID, out var expires))
            {
                return false;
            }
            if (Time.realtimeSinceStartup > expires)
            {
                spawnProtection.Remove(player.userID);
                return false;
            }
            return true;
        }
        #endregion

        #region Flags
        private void ResetFlags()
        {
            flagCarrierA = 0;
            flagCarrierB = 0;
            droppedFlagA = null;
            droppedFlagB = null;
            ClearDroppedFlag(Team.TeamA);
            ClearDroppedFlag(Team.TeamB);
        }

        private void StartHudUpdates()
        {
            hudTimer?.Destroy();
            hudTimer = timer.Every(0.5f, () =>
            {
                if (currentMode == GameMode.CTF)
                {
                    CheckFlagInteractions();
                }
                foreach (var session in sessions.Values)
                {
                    UpdateHud(session.Player);
                }
            });
        }

        private void StopHudUpdates()
        {
            hudTimer?.Destroy();
            hudTimer = null;
        }

        private void CheckFlagInteractions()
        {
            var arena = GetActiveArena();
            if (arena == null)
            {
                return;
            }
            foreach (var session in sessions.Values)
            {
                var player = session.Player;
                if (!player || !player.IsConnected)
                {
                    continue;
                }
                if (session.Team == Team.TeamA && droppedFlagB.HasValue && Vector3.Distance(player.transform.position, droppedFlagB.Value) < FlagInteractionDistance)
                {
                    ClearDroppedFlag(Team.TeamB);
                    EquipFlag(player, Team.TeamB);
                }
                if (session.Team == Team.TeamB && droppedFlagA.HasValue && Vector3.Distance(player.transform.position, droppedFlagA.Value) < FlagInteractionDistance)
                {
                    ClearDroppedFlag(Team.TeamA);
                    EquipFlag(player, Team.TeamA);
                }
                if (session.Team == Team.TeamA && flagCarrierB == 0 && Vector3.Distance(player.transform.position, arena.FlagBBase) < FlagInteractionDistance)
                {
                    EquipFlag(player, Team.TeamB);
                }
                if (session.Team == Team.TeamB && flagCarrierA == 0 && Vector3.Distance(player.transform.position, arena.FlagABase) < FlagInteractionDistance)
                {
                    EquipFlag(player, Team.TeamA);
                }
                if (session.Team == Team.TeamA && flagCarrierB == player.userID && Vector3.Distance(player.transform.position, arena.FlagABase) < FlagInteractionDistance)
                {
                    teamAScore++;
                    flagCarrierB = 0;
                    ClearDroppedFlag(Team.TeamB);
                    CheckMatchEnd();
                }
                if (session.Team == Team.TeamB && flagCarrierA == player.userID && Vector3.Distance(player.transform.position, arena.FlagBBase) < FlagInteractionDistance)
                {
                    teamBScore++;
                    flagCarrierA = 0;
                    ClearDroppedFlag(Team.TeamA);
                    CheckMatchEnd();
                }
            }
        }

        private void EquipFlag(BasePlayer player, Team flagTeam)
        {
            var item = ItemManager.CreateByName(FlagShortname, 1);
            if (item == null)
            {
                return;
            }
            if (!item.MoveToContainer(player.inventory.containerBelt))
            {
                item.MoveToContainer(player.inventory.containerWear);
            }
            player.UpdateActiveItem(item.uid);
            if (flagTeam == Team.TeamA)
            {
                flagCarrierA = player.userID;
                droppedFlagA = null;
            }
            else
            {
                flagCarrierB = player.userID;
                droppedFlagB = null;
            }
        }

        private void DropFlag(BasePlayer player, Team flagTeam)
        {
            var definition = ItemManager.FindItemDefinition(FlagShortname);
            var itemId = definition != null ? definition.itemid : 0;
            var item = player.inventory.containerBelt?.FindItemByItemID(itemId)
                       ?? player.inventory.containerWear?.FindItemByItemID(itemId);
            item?.RemoveFromContainer();
            var dropped = item?.Drop(player.transform.position + player.transform.forward, Vector3.zero);
            var dropPosition = dropped?.transform.position ?? player.transform.position;
            if (flagTeam == Team.TeamA)
            {
                flagCarrierA = 0;
                droppedFlagA = dropPosition;
                droppedFlagEntityA = dropped;
            }
            else
            {
                flagCarrierB = 0;
                droppedFlagB = dropPosition;
                droppedFlagEntityB = dropped;
            }
        }

        private void ClearDroppedFlag(Team flagTeam)
        {
            if (flagTeam == Team.TeamA)
            {
                droppedFlagEntityA?.Kill();
                droppedFlagEntityA = null;
                droppedFlagA = null;
            }
            else
            {
                droppedFlagEntityB?.Kill();
                droppedFlagEntityB = null;
                droppedFlagB = null;
            }
        }
        #endregion

        #region UI
        private void ShowMainUi(BasePlayer player)
        {
            DestroyAllUi(player);
            var data = GetPlayerData(player.userID);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = DarkPanelColor },
                RectTransform = { AnchorMin = "0.25 0.18", AnchorMax = "0.75 0.82" },
                CursorEnabled = true
            }, "Overlay", MainUi);

            AddBackgroundImage(container, MainUi);

            container.Add(new CuiLabel
            {
                Text = { Text = "Paintball Ultra", FontSize = 26, Align = TextAnchor.UpperCenter, Color = TextColor },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.98" }
            }, MainUi);

            container.Add(new CuiLabel
            {
                Text = { Text = $"K/D: {data.Kills}/{data.Deaths}", FontSize = 16, Align = TextAnchor.MiddleLeft, Color = TextColor },
                RectTransform = { AnchorMin = "0.08 0.68", AnchorMax = "0.5 0.76" }
            }, MainUi);

            container.Add(new CuiLabel
            {
                Text = { Text = $"PaintChips: {data.PaintChips}", FontSize = 16, Align = TextAnchor.MiddleLeft, Color = TextColor },
                RectTransform = { AnchorMin = "0.08 0.6", AnchorMax = "0.5 0.68" }
            }, MainUi);

            container.Add(new CuiButton
            {
                Button = { Color = TeamAColor, Command = "pbui.play", Close = MainUi },
                RectTransform = { AnchorMin = "0.3 0.18", AnchorMax = "0.7 0.28" },
                Text = { Text = "Play", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = TextColor }
            }, MainUi);

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.25 0.9", Command = "pbui.shop" },
                RectTransform = { AnchorMin = "0.3 0.08", AnchorMax = "0.7 0.16" },
                Text = { Text = "Shop", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = TextColor }
            }, MainUi);

            CuiHelper.AddUi(player, container);
        }

        private void ShowLobbyUi(BasePlayer player)
        {
            DestroyUi(player, LobbyUi);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = DarkPanelColor },
                RectTransform = { AnchorMin = "0.02 0.2", AnchorMax = "0.28 0.8" },
                CursorEnabled = true
            }, "Overlay", LobbyUi);

            AddBackgroundImage(container, LobbyUi);

            container.Add(new CuiLabel
            {
                Text = { Text = "Lobby", FontSize = 18, Align = TextAnchor.UpperCenter, Color = TextColor },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, LobbyUi);

            var y = 0.78f;
            foreach (var session in sessions.Values)
            {
                var status = readyPlayers.Contains(session.Player.userID) ? "Ready" : "Not Ready";
                var statusColor = readyPlayers.Contains(session.Player.userID) ? TeamAColor : TeamBColor;
                container.Add(new CuiLabel
                {
                    Text = { Text = $"{session.Player.displayName} - {status}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = statusColor },
                    RectTransform = { AnchorMin = $"0.08 {y}", AnchorMax = $"0.92 {y + 0.05f}" }
                }, LobbyUi);
                y -= 0.06f;
                if (y < 0.1f)
                {
                    break;
                }
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.25 0.9", Command = "pbui.ready" },
                RectTransform = { AnchorMin = "0.2 0.02", AnchorMax = "0.8 0.09" },
                Text = { Text = "Toggle Ready", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TextColor }
            }, LobbyUi);

            CuiHelper.AddUi(player, container);
        }

        private void ShowVotingUi(BasePlayer player)
        {
            DestroyUi(player, VotingUi);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = DarkPanelColor },
                RectTransform = { AnchorMin = "0.25 0.15", AnchorMax = "0.75 0.85" },
                CursorEnabled = true
            }, "Overlay", VotingUi);

            AddBackgroundImage(container, VotingUi);

            container.Add(new CuiLabel
            {
                Text = { Text = "Vote for Arena", FontSize = 20, Align = TextAnchor.UpperCenter, Color = TextColor },
                RectTransform = { AnchorMin = "0 0.9", AnchorMax = "1 1" }
            }, VotingUi);

            var startX = 0.08f;
            var width = 0.26f;
            for (var i = 0; i < voteOrder.Count; i++)
            {
                var arenaName = voteOrder[i];
                if (!storedData.Arenas.TryGetValue(arenaName, out var arena))
                {
                    continue;
                }
                var anchorMin = $"{startX + i * (width + 0.04f)} 0.2";
                var anchorMax = $"{startX + i * (width + 0.04f) + width} 0.75";
                var image = GetImage(arena.Thumbnail ?? "panel");
                container.Add(new CuiElement
                {
                    Parent = VotingUi,
                    Components =
                    {
                        new CuiRawImageComponent { Png = image, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = anchorMin, AnchorMax = anchorMax }
                    }
                });
                container.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = $"pbui.vote {arenaName}" },
                    RectTransform = { AnchorMin = anchorMin, AnchorMax = anchorMax },
                    Text = { Text = $"{arenaName}\nVotes: {GetVotes(arenaName)}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TextColor }
                }, VotingUi);
            }

            CuiHelper.AddUi(player, container);
        }

        private void ShowShopUi(BasePlayer player)
        {
            DestroyUi(player, ShopUi);
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = DarkPanelColor },
                RectTransform = { AnchorMin = "0.2 0.1", AnchorMax = "0.8 0.9" },
                CursorEnabled = true
            }, "Overlay", ShopUi);

            AddBackgroundImage(container, ShopUi);

            container.Add(new CuiLabel
            {
                Text = { Text = "Paintball Shop", FontSize = 20, Align = TextAnchor.UpperCenter, Color = TextColor },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, ShopUi);

            var data = GetPlayerData(player.userID);
            var y = 0.75f;
            foreach (var look in config.CosmeticLooks)
            {
                var unlocked = data.UnlockedLooks.Contains(look.Name);
                var selected = data.ActiveLook == look.Name;
                var label = unlocked ? (selected ? "Active" : "Unlocked") : $"{look.Cost} Chips";
                var buttonCommand = unlocked ? $"pbui.selectlook {look.Name}" : $"pbui.buy {look.Name}";
                container.Add(new CuiLabel
                {
                    Text = { Text = $"{look.Name}", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = TextColor },
                    RectTransform = { AnchorMin = $"0.08 {y}", AnchorMax = $"0.5 {y + 0.05f}" }
                }, ShopUi);
                container.Add(new CuiButton
                {
                    Button = { Color = unlocked ? TeamAColor : TeamBColor, Command = buttonCommand },
                    RectTransform = { AnchorMin = $"0.62 {y}", AnchorMax = $"0.88 {y + 0.05f}" },
                    Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = TextColor }
                }, ShopUi);
                y -= 0.08f;
                if (y < 0.15f)
                {
                    break;
                }
            }

            CuiHelper.AddUi(player, container);
        }

        private void UpdateHud(BasePlayer player)
        {
            DestroyUi(player, HudUi);
            if (currentPhase != GamePhase.Match && currentPhase != GamePhase.Warmup)
            {
                return;
            }
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.74 0.86", AnchorMax = "0.98 0.98" }
            }, "Overlay", HudUi);

            var modeText = $"Mode: {currentMode}";
            container.Add(new CuiLabel
            {
                Text = { Text = modeText, FontSize = 12, Align = TextAnchor.UpperLeft, Color = TextColor },
                RectTransform = { AnchorMin = "0.05 0.6", AnchorMax = "0.95 0.95" }
            }, HudUi);

            var scoreText = currentMode == GameMode.FFA
                ? $"Top: {sessions.Values.OrderByDescending(x => x.Score).FirstOrDefault()?.Score ?? 0}/{config.ScoreLimit}"
                : $"{teamAScore} - {teamBScore}";

            container.Add(new CuiLabel
            {
                Text = { Text = scoreText, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = TextColor },
                RectTransform = { AnchorMin = "0.05 0.3", AnchorMax = "0.95 0.6" }
            }, HudUi);

            CuiHelper.AddUi(player, container);
        }

        private void ShowKill(string attackerName, string victimName)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!sessions.ContainsKey(player.userID))
                {
                    continue;
                }
                var targetPlayer = player;
                killFeed[player.userID] = $"{attackerName} → {victimName}";
                DestroyUi(player, KillFeedUi);
                var container = new CuiElementContainer();
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0.7 0.78", AnchorMax = "0.98 0.86" }
                }, "Overlay", KillFeedUi);
                container.Add(new CuiLabel
                {
                    Text = { Text = killFeed[player.userID], FontSize = 12, Align = TextAnchor.MiddleRight, Color = TextColor },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, KillFeedUi);
                CuiHelper.AddUi(player, container);
                timer.Once(3f, () =>
                {
                    if (targetPlayer != null && targetPlayer.IsConnected)
                    {
                        DestroyUi(targetPlayer, KillFeedUi);
                    }
                });
            }
        }

        private void UpdateLobbyUi()
        {
            foreach (var session in sessions.Values)
            {
                ShowLobbyUi(session.Player);
            }
        }

        private void UpdateVotingUi()
        {
            foreach (var session in sessions.Values)
            {
                ShowVotingUi(session.Player);
            }
        }

        private void DestroyAllUi(BasePlayer player)
        {
            DestroyUi(player, MainUi);
            DestroyUi(player, LobbyUi);
            DestroyUi(player, VotingUi);
            DestroyUi(player, HudUi);
            DestroyUi(player, ShopUi);
            DestroyUi(player, KillFeedUi);
        }

        private void DestroyUi(BasePlayer player, string panel) => CuiHelper.DestroyUi(player, panel);

        private void AddBackgroundImage(CuiElementContainer container, string parent)
        {
            var image = GetImage("background");
            if (string.IsNullOrEmpty(image))
            {
                return;
            }
            container.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiRawImageComponent { Png = image, Color = "1 1 1 0.9" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });
        }
        #endregion

        #region Voting
        private void PrepareVoting()
        {
            voteOrder.Clear();
            voteCounts.Clear();
            voteSelections.Clear();
            var arenas = storedData.Arenas.Keys.ToList();
            if (arenas.Count == 0)
            {
                return;
            }
            var options = arenas.OrderBy(_ => rng.Next()).Take(config.MaxVoteOptions);
            foreach (var arenaName in options)
            {
                voteOrder.Add(arenaName);
                voteCounts[arenaName] = 0;
            }
        }

        private void HandleVote(BasePlayer player, string arenaName)
        {
            if (!voteCounts.ContainsKey(arenaName))
            {
                return;
            }
            if (voteSelections.TryGetValue(player.userID, out var prev) && voteCounts.ContainsKey(prev))
            {
                voteCounts[prev] = Mathf.Max(0, voteCounts[prev] - 1);
            }
            voteSelections[player.userID] = arenaName;
            voteCounts[arenaName]++;
        }

        private int GetVotes(string arenaName) => voteCounts.TryGetValue(arenaName, out var count) ? count : 0;

        private void DetermineVoteWinner()
        {
            if (voteCounts.Count == 0)
            {
                return;
            }
            activeArena = voteCounts.OrderByDescending(x => x.Value).First().Key;
        }
        #endregion

        #region Shop
        private void PurchaseLook(BasePlayer player, string lookName)
        {
            var look = config.CosmeticLooks.FirstOrDefault(x => x.Name == lookName);
            if (look == null)
            {
                return;
            }
            var data = GetPlayerData(player.userID);
            if (data.UnlockedLooks.Contains(look.Name))
            {
                return;
            }
            if (data.PaintChips < look.Cost)
            {
                return;
            }
            data.PaintChips -= look.Cost;
            data.UnlockedLooks.Add(look.Name);
            SaveDataAsync();
        }

        private void SelectLook(BasePlayer player, string lookName)
        {
            var data = GetPlayerData(player.userID);
            if (!data.UnlockedLooks.Contains(lookName))
            {
                return;
            }
            data.ActiveLook = lookName;
            SaveDataAsync();
        }
        #endregion

        #region Arena Management
        private void GiveWand(BasePlayer player)
        {
            var item = ItemManager.CreateByName(WandItemShortname, 1);
            if (item == null)
            {
                return;
            }
            item.name = "Paintball Wand";
            item.MoveToContainer(player.inventory.containerBelt);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!wandModes.TryGetValue(player.userID, out var mode))
            {
                return;
            }
            var activeItem = player.GetActiveItem();
            if (activeItem == null || activeItem.info.shortname != WandItemShortname)
            {
                return;
            }
            if (input.WasJustPressed(BUTTON.FIRE_PRIMARY))
            {
                var position = GetLookPoint(player);
                if (!position.HasValue)
                {
                    return;
                }
                var arena = GetActiveArena();
                if (arena == null)
                {
                    return;
                }
                switch (mode)
                {
                    case WandMode.Lobby:
                        arena.LobbyPosition = position.Value;
                        break;
                    case WandMode.SpawnA:
                        arena.TeamASpawns.Add(position.Value);
                        break;
                    case WandMode.SpawnB:
                        arena.TeamBSpawns.Add(position.Value);
                        break;
                    case WandMode.FFA:
                        arena.FFASpawns.Add(position.Value);
                        break;
                    case WandMode.FlagA:
                        arena.FlagABase = position.Value;
                        break;
                    case WandMode.FlagB:
                        arena.FlagBBase = position.Value;
                        break;
                }
                SaveDataAsync();
                player.ChatMessage($"{mode} location saved.");
            }
        }

        private Vector3? GetLookPoint(BasePlayer player)
        {
            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, WandMaxRaycastDistance))
            {
                return hit.point;
            }
            return null;
        }
        #endregion

        #region Helpers
        private void RegisterImages()
        {
            if (ImageLibrary == null)
            {
                return;
            }
            foreach (var entry in config.ImageUrls)
            {
                ImageLibrary.Call("AddImage", entry.Value, entry.Key);
            }
            foreach (var arena in storedData.Arenas.Values)
            {
                if (!string.IsNullOrEmpty(arena.Thumbnail))
                {
                    ImageLibrary.Call("AddImage", arena.Thumbnail, arena.Thumbnail);
                }
            }
        }

        private string GetImage(string name)
        {
            if (ImageLibrary == null)
            {
                return null;
            }
            return ImageLibrary.Call("GetImage", name) as string;
        }

        private bool IsFriendlyFire(BasePlayer attacker, BasePlayer victim)
        {
            if (currentMode == GameMode.FFA)
            {
                return false;
            }
            var attackerTeam = sessions[attacker.userID].Team;
            var victimTeam = sessions[victim.userID].Team;
            return attackerTeam == victimTeam;
        }

        private void RefillAmmo(BasePlayer player)
        {
            var projectile = player.GetHeldEntity() as BaseProjectile;
            if (projectile == null)
            {
                return;
            }
            projectile.primaryMagazine.contents = projectile.primaryMagazine.capacity;
            projectile.SendNetworkUpdateImmediate();
        }

        private PlayerData GetPlayerData(ulong userId)
        {
            if (!storedData.Players.TryGetValue(userId, out var data))
            {
                data = new PlayerData();
                storedData.Players[userId] = data;
            }
            return data;
        }

        private Arena GetActiveArena()
        {
            if (string.IsNullOrEmpty(activeArena))
            {
                return null;
            }
            storedData.Arenas.TryGetValue(activeArena, out var arena);
            return arena;
        }

        private StoredData CloneData()
        {
            var clone = new StoredData();
            foreach (var entry in storedData.Players)
            {
                var data = entry.Value;
                clone.Players[entry.Key] = new PlayerData
                {
                    Kills = data.Kills,
                    Wins = data.Wins,
                    Deaths = data.Deaths,
                    PaintChips = data.PaintChips,
                    ActiveLook = data.ActiveLook,
                    UnlockedLooks = data.UnlockedLooks != null
                        ? new HashSet<string>(data.UnlockedLooks)
                        : new HashSet<string>()
                };
            }
            foreach (var entry in storedData.Arenas)
            {
                var arena = entry.Value;
                clone.Arenas[entry.Key] = new Arena
                {
                    Name = arena.Name,
                    LobbyPosition = arena.LobbyPosition,
                    TeamASpawns = new List<Vector3>(arena.TeamASpawns ?? new List<Vector3>()),
                    TeamBSpawns = new List<Vector3>(arena.TeamBSpawns ?? new List<Vector3>()),
                    FFASpawns = new List<Vector3>(arena.FFASpawns ?? new List<Vector3>()),
                    FlagABase = arena.FlagABase,
                    FlagBBase = arena.FlagBBase,
                    Thumbnail = arena.Thumbnail
                };
            }
            return clone;
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName) ?? new StoredData();
            }
            catch
            {
                storedData = new StoredData();
            }
        }

        private void SaveDataAsync()
        {
            if (Interlocked.Exchange(ref savingData, 1) == 1)
            {
                return;
            }
            var snapshot = CloneData();
            Task.Run(() =>
            {
                try
                {
                    Interface.Oxide.DataFileSystem.WriteObject(DataFileName, snapshot);
                }
                catch (Exception ex)
                {
                    Interface.Oxide.LogError($"PaintballUltra save failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref savingData, 0);
                }
            });
        }
        #endregion

        #region Data Models
        private class StoredData
        {
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
            public Dictionary<string, Arena> Arenas = new Dictionary<string, Arena>();
        }

        private class PlayerData
        {
            public int Kills;
            public int Wins;
            public int Deaths;
            public int PaintChips;
            public HashSet<string> UnlockedLooks = new HashSet<string>();
            public string ActiveLook;
        }

        private class Arena
        {
            public string Name;
            public Vector3 LobbyPosition;
            public List<Vector3> TeamASpawns = new List<Vector3>();
            public List<Vector3> TeamBSpawns = new List<Vector3>();
            public List<Vector3> FFASpawns = new List<Vector3>();
            public Vector3 FlagABase;
            public Vector3 FlagBBase;
            public string Thumbnail;
        }

        private class PlayerSession
        {
            public BasePlayer Player;
            public Team Team;
            public int Score;
            public bool IsAlive;

            public PlayerSession(BasePlayer player)
            {
                Player = player;
            }
        }

        private class CosmeticLook
        {
            public string Name;
            public int Cost;
            public List<string> Items = new List<string>();
        }

        private class InventorySnapshot
        {
            public List<ItemSnapshot> Main;
            public List<ItemSnapshot> Belt;
            public List<ItemSnapshot> Wear;
            public MetabolismSnapshot Metabolism;
        }

        private class ItemSnapshot
        {
            public string ShortName;
            public int Amount;
            public ulong Skin;
            public float Condition;
            public int Ammo;
            public string AmmoType;
            public List<ItemSnapshot> Contents;

            public static ItemSnapshot From(Item item)
            {
                var snapshot = new ItemSnapshot
                {
                    ShortName = item.info.shortname,
                    Amount = item.amount,
                    Skin = item.skin,
                    Condition = item.condition,
                    Contents = item.contents?.itemList.Select(From).ToList()
                };
                var weapon = item.GetHeldEntity() as BaseProjectile;
                if (weapon != null)
                {
                    snapshot.Ammo = weapon.primaryMagazine.contents;
                    snapshot.AmmoType = weapon.primaryMagazine.ammoType?.shortname;
                }
                return snapshot;
            }

            public void Restore(ItemContainer container)
            {
                var item = ItemManager.CreateByName(ShortName, Amount, Skin);
                if (item == null)
                {
                    return;
                }
                item.condition = Condition;
                if (item.contents != null && Contents != null)
                {
                    foreach (var child in Contents)
                    {
                        child.Restore(item.contents);
                    }
                }
                item.MoveToContainer(container);
                var weapon = item.GetHeldEntity() as BaseProjectile;
                if (weapon != null && !string.IsNullOrEmpty(AmmoType))
                {
                    weapon.primaryMagazine.contents = Ammo;
                    var ammoDef = ItemManager.FindItemDefinition(AmmoType);
                    if (ammoDef != null)
                    {
                        weapon.primaryMagazine.ammoType = ammoDef;
                    }
                }
            }
        }

        private class MetabolismSnapshot
        {
            public float Calories;
            public float Hydration;
            public float Health;

            public static MetabolismSnapshot From(BasePlayer player)
            {
                return new MetabolismSnapshot
                {
                    Calories = player.metabolism.calories.value,
                    Hydration = player.metabolism.hydration.value,
                    Health = player.health
                };
            }

            public void Apply(BasePlayer player)
            {
                player.metabolism.calories.value = Calories;
                player.metabolism.hydration.value = Hydration;
                player.health = Health;
            }
        }
        #endregion

        #region Enums
        private enum GamePhase
        {
            Lobby,
            Voting,
            Warmup,
            Match,
            PostGame
        }

        private enum GameMode
        {
            TDM,
            FFA,
            Elimination,
            CTF
        }

        private enum Team
        {
            None,
            TeamA,
            TeamB
        }

        private enum WandMode
        {
            Lobby,
            SpawnA,
            SpawnB,
            FFA,
            FlagA,
            FlagB
        }
        #endregion
    }
}
