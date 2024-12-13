using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Constants;
using Oxide.Ext.Discord.Entities;
using Oxide.Ext.Discord.Logging;
using Oxide.Ext.Discord.Clients;
using Oxide.Ext.Discord.Connections;
using Oxide.Ext.Discord.Interfaces;
using Oxide.Ext.Discord.Libraries;
using Random = Oxide.Core.Random;

namespace Oxide.Plugins
{


    [Info("Discord Status", "Gonzi", "4.1.0")]
    [Description("Shows server information as a discord bot status")]

    public class DiscordStatus : CovalencePlugin , IDiscordPlugin
    {
        private string seperatorText = string.Join("-", new string[25 + 1]);
        private bool enableChatSeparators;

        #region Fields

        public DiscordClient Client { get; set; }
        
        [PluginReference]
        private Plugin WipeInfoApi;

        private readonly BotConnection _settings = new BotConnection
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers | GatewayIntents.MessageContent
        };
        
        private DiscordGuild _guild;
        
        private readonly DiscordLink _link = GetLibrary<DiscordLink>();

        Configuration config;
        private int statusIndex = -1;
        private string[] StatusTypes = new string[]
        {
            "Game",
            "Stream",
            "Listen",
            "Watch"
        };

        #endregion

        #region Config
        class Configuration
        {
            [JsonProperty(PropertyName = "Discord Bot Token")]
            public string BotToken = string.Empty;
            
            [JsonProperty(PropertyName = "Discord Server ID (Optional if bot only in 1 guild)")]
            public Snowflake GuildId { get; set; }

            [JsonProperty(PropertyName = "Prefix")]
            public string Prefix = "!";

            [JsonProperty(PropertyName = "Discord Group Id needed for Commands (null to disable)")]
            public Snowflake? GroupId;

            [JsonProperty(PropertyName = "Discord Channel Id needed for Commands (null to disable)")]
            public Snowflake? ChannelId;

            [JsonProperty(PropertyName = "Update Interval (Seconds)")]
            public int UpdateInterval = 5;

            [JsonProperty(PropertyName = "Randomize Status")]
            public bool Randomize = false;

            [JsonProperty(PropertyName = "Status Type (Game/Stream/Listen/Watch)")]
            public string StatusType = "Game";

            [JsonProperty(PropertyName = "Status", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Status = new List<string>
            {
                "{players.online} / {server.maxplayers} Online!",
                "{server.entities} Entities",
                "{players.sleepers} Sleepers!",
                "{players.authenticated} Linked Account(s)"
            };
            
            [JsonProperty(PropertyName = "Delete Command Message")]
            public bool Delete = false;
            
            [JsonConverter(typeof(StringEnumConverter))]
            [DefaultValue(DiscordLogLevel.Info)]
            [JsonProperty(PropertyName = "Discord Extension Log Level (Verbose, Debug, Info, Warning, Error, Exception, Off)")]
            public DiscordLogLevel ExtensionDebugging { get; set; } = DiscordLogLevel.Info;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
            }
            catch
            {
                Config.WriteObject(config, false, $"{Interface.Oxide.ConfigDirectory}/{Name}.jsonError");
                PrintError("The configuration file contains an error and has been replaced with a default config.\n" +
                           "The error configuration file was saved with the .jsonError extension");
                LoadDefaultConfig();
            }

            SaveConfig();
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Lang
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Title"] = "Players List",
                ["Players"] = "Online Players [{0}/{1}] 🎆\n {2}",
                ["IPAddress"] = "steam://connect/{0}:{1}",
				["NEXTWipe"] = "**Current Wipe:** {2}\n**Next Wipe:** {0} \n**Days Until Wipe:** {1}"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Title"] = "플레이어 목록",
                ["Players"] = "접속중인 플레이어 [{0}/{1}] 🎆\n {2}",
                ["IPAddress"] = "steam://connect/{0}:{1}",
                ["NEXTWipe"] = "**현재 와이프:** {2}\n**다음 와이프:** {0} \n**와이프까지 남은 일수:** {1}"
            }, this, "kr");
        }

        private string Lang(string key, params object[] args)
        {
            return string.Format(lang.GetMessage(key, this), args);
        }

        #endregion

        #region Discord
        public DiscordEmbed ServerStats(string content)
        {
            DiscordEmbed embed = new DiscordEmbed
            {
                Title = Lang("Title", ConVar.Server.hostname),
                Description = content,
                Thumbnail = new EmbedThumbnail
                {
                    Url = $"{ConVar.Server.headerimage}"
                },
                Footer = new EmbedFooter
                {
                    Text = $"Gonzi V{Version}",
                    IconUrl = "https://cdn.discordapp.com/avatars/321373026488811520/08f996472c573473e7e30574e0e28da0.png"
                },

                Color = new DiscordColor(15158332)
            };
            return embed;
        }
        [HookMethod(DiscordExtHooks.OnDiscordGuildMessageCreated)]
        void OnDiscordGuildMessageCreated(DiscordMessage message)
        {
            if (message.Author.Bot == true) return;

            if (!string.IsNullOrEmpty(message.Content) && message.Content[0] == config.Prefix[0])
            {
                string cmd;
                try
                {
                    cmd = message.Content.Split(' ')[0].ToLower();
                    if (string.IsNullOrEmpty(cmd.Trim()))
                        cmd = message.Content.Trim().ToLower();
                }
                catch
                {
                    cmd = message.Content.Trim().ToLower();
                }

                cmd = cmd.Remove(0, 1);
                cmd = cmd.Trim().ToLower();

                DiscordCMD(cmd, message);
            }
        }

        private void DiscordCMD(string command, DiscordMessage message)
        {
            if (config.GroupId.HasValue && !message.Member.Roles.Contains(config.GroupId.Value)) return;
            if (config.ChannelId.HasValue && !(message.ChannelId == config.ChannelId.Value)) return;
            switch (command)
            {
                case "players":
                    {
                        string list = string.Empty;
                        var playerList = BasePlayer.activePlayerList;
                        foreach (var player in playerList)
                        {
                            list += $"[{player.displayName}](https://steamcommunity.com/profiles/{player.UserIDString}/) \n";
                        }

                        DiscordChannel.Get(Client, message.ChannelId).Then(channel =>
                        {
                            channel.CreateMessage(Client, ServerStats(Lang("Players", BasePlayer.activePlayerList.Count, ConVar.Server.maxplayers, list)));
                        });
                        break;
                    }
                case "ip":
                    {
                        DiscordChannel.Get(Client, message.ChannelId).Then(channel =>
                        {
                            webrequest.Enqueue("http://icanhazip.com", "", (code, response) =>
                            {
                                string ip = response.Trim();
                                channel.CreateMessage(Client, Lang("IPAddress", ip, ConVar.Server.port));
                            }, this);
                        });
                        break;
                    }
                    case "wipe":
					{
			            if (WipeInfoApi==null)
						{
							DiscordChannel.Get(Client, message.ChannelId).Then(channel =>
										{
                                channel.CreateMessage(Client, "Not Loaded");
										});
                        }
						else
						{
							DiscordChannel.Get(Client, message.ChannelId).Then(channel =>
										{
											channel.CreateMessage(Client, ServerStats(Lang("NEXTWipe",GetNextWipe().ToString("dddd, dd MMMM yyyy"),GetDaysTillWipe().ToString(),GetCurrentWipe().ToString("dddd, dd MMMM yyyy"))));
										});
						}
                        break;
					}
                    default:
                        return;
            }
            if (config.Delete)
                message.Delete(Client);
 
        }

        #endregion

        #region Oxide Hooks
        private void OnServerInitialized()
        {
            lang.SetServerLanguage("en");

            if (string.IsNullOrEmpty(config.BotToken))
                return;

            _settings.ApiToken = config.BotToken;
            _settings.LogLevel = config.ExtensionDebugging;
            Client.Connect(_settings);

            timer.Every(config.UpdateInterval, () => UpdateStatus());
        }
        
        [HookMethod(DiscordExtHooks.OnDiscordGatewayReady)]
        private void OnDiscordGatewayReady(GatewayReadyEvent ready) {
            if (ready.Guilds.Count == 0)
            {
                PrintError("Your bot was not found in any discord servers. Please invite it to a server and reload the plugin.");
                return;
            }

            DiscordGuild guild = null;
            if (ready.Guilds.Count == 1 && !config.GuildId.IsValid())
            {
                guild = ready.Guilds.Values.FirstOrDefault();
            }

            if (guild == null)
            {
                guild = ready.Guilds[config.GuildId];
            }

            if (guild == null)
            {
                PrintError("Failed to find a matching guild for the Discord Server Id. " +
                           "Please make sure your guild Id is correct and the bot is in the discord server.");
                return;
            }
                
            if (Client.Bot.Application.Flags.HasValue && Client.Bot.Application.Flags.Value.HasFlag(ApplicationFlags.GatewayGuildMembersLimited) == false)
            {
                PrintError($"You need to enable \"Server Members Intent\" for {Client.Bot.BotUser.Username} @ https://discord.com/developers/applications\n" +
                        $"{Name} will not function correctly until that is fixed. Once updated please reload {Name}.");
                return;
            }
            
            _guild = guild;
        }
        #endregion

        #region Status Update
        private void UpdateStatus()
        {
            try
            {
                if (config.Status.Count == 0)
                    return;

                var index = GetStatusIndex();

                Client.UpdateStatus(new UpdatePresenceCommand
                {
                    Activities = new List<DiscordActivity>
                    {
                        new DiscordActivity
                        {
                            Name = Format(config.Status[index]),
                            Type = GetStatusType()
                        }
                    }
                });

                statusIndex = index;
            }
            catch (Exception err)
            {
                LogToFile("DiscordStatus", $"{err}", this);
            }
        }
        #endregion

        #region Helper Methods
        private int GetStatusIndex()
        {
            if (!config.Randomize)
                return (statusIndex + 1) % config.Status.Count;

            var index = 0;
            do index = Random.Range(0, config.Status.Count);
            while (index == statusIndex);

            return index;
        }

        private ActivityType GetStatusType()
        {
            if (!StatusTypes.Contains(config.StatusType))
                PrintError($"Unknown Status Type '{config.StatusType}'");

            return config.StatusType switch
            {
                "Game" => ActivityType.Game,
                "Stream" => ActivityType.Streaming,
                "Listen" => ActivityType.Listening,
                "Watch" => ActivityType.Watching,
                _ => ActivityType.Game,
            };
        }

        private string Format(string message)
        {
            message = message
                .Replace("{guild.name}", _guild.Name ?? "{unknown}")
                .Replace("{members.total}", _guild.MemberCount?.ToString() ?? "{unknown}")
                .Replace("{channels.total}", _guild.Channels?.Count.ToString() ?? "{unknown}")
                .Replace("{server.hostname}", server.Name)
                .Replace("{server.maxplayers}", server.MaxPlayers.ToString())
                .Replace("{players.online}", players.Connected.Count().ToString())
                .Replace("{players.authenticated}", GetAuthCount().ToString())
                .Replace("{days.untilwipe}", WipeInfoApi != null ? GetDaysTillWipe().ToString() : "{unknown}");

#if RUST
            message = message
                .Replace("{server.ip}", ConVar.Server.ip)
                .Replace("{server.port}", ConVar.Server.port.ToString())
                .Replace("{server.entities}", BaseNetworkable.serverEntities.Count.ToString())
                .Replace("{server.worldsize}", ConVar.Server.worldsize.ToString())
                .Replace("{server.seed}", ConVar.Server.seed.ToString())
                .Replace("{server.fps}", Performance.current.frameRate.ToString())
                .Replace("{server.avgfps}", Convert.ToInt32(Performance.current.frameRateAverage).ToString())
                .Replace("{players.queued}", ConVar.Admin.ServerInfo().Queued.ToString())
                .Replace("{players.joining}", ConVar.Admin.ServerInfo().Joining.ToString())
                .Replace("{players.sleepers}", BasePlayer.sleepingPlayerList.Count.ToString())
                .Replace("{players.total}", (players.Connected.Count() + BasePlayer.sleepingPlayerList.Count).ToString());
#endif

            return message;
        }

        private int GetAuthCount() => _link.LinkedCount;
		private DateTime GetNextWipe() => (DateTime)WipeInfoApi.Call("GetNextWipe");
		private int GetDaysTillWipe() => (int)WipeInfoApi.Call("GetDaysTillWipe");
		private DateTime GetCurrentWipe() => (DateTime)WipeInfoApi.Call("GetCurrentWipe");
        #endregion
    }
}