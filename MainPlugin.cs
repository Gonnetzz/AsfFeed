using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace ASFLeaderboardPlugin {
    public class PluginConfig {
        public int Port { get; set; } = 12345;
        public uint AppID { get; set; } = 12345;
        public string LeaderboardName { get; set; } = "Leaderboard_name";
        public bool Debug { get; set; } = false;
        public Dictionary<string, List<Dictionary<string, string>>> PredefinedFilters { get; set; } = new();
    }

    [Export(typeof(IPlugin))]
    public class MainPlugin : IPlugin, IBotSteamClient {
        public string Name => "ASF Leaderboard XML Host";
        public Version Version => new Version(2, 9, 0, 0);

        public static PluginConfig Config { get; private set; } = new PluginConfig();
        private static Bot? _workingBot = null;
        private static readonly Leaderboard _leaderboard = new Leaderboard();
        private static readonly Lobby _lobby = new Lobby();

        public Task OnLoaded() {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;
            string configPath = Path.Combine(Path.GetDirectoryName(assemblyLocation) ?? "", "settings.json");

            if (File.Exists(configPath)) {
                try {
                    string json = File.ReadAllText(configPath);
                    var loadedConfig = JsonSerializer.Deserialize<PluginConfig>(json);
                    if (loadedConfig != null) {
                        Config = loadedConfig;
                    }
                }
                catch (Exception ex) {
                    ASF.ArchiLogger.LogGenericError($"Failed to load settings.json: {ex.Message}");
                }
            }
            else {
                ASF.ArchiLogger.LogGenericError($"settings.json not found at {configPath}. Using defaults.");
            }

            _ = Task.Run(async () => await StartHttpServer());
            return Task.CompletedTask;
        }

        public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
            if (_workingBot == null && bot.IsConnectedAndLoggedOn) {
                _workingBot = bot;
            }

            callbackManager.Subscribe<global::SteamKit2.SteamUserStats.FindOrCreateLeaderboardCallback>(_leaderboard.OnFindLeaderboard);
            callbackManager.Subscribe<global::SteamKit2.SteamUserStats.LeaderboardEntriesCallback>(_leaderboard.OnLeaderboardScores);
            callbackManager.Subscribe<global::SteamKit2.SteamFriends.PersonaStateCallback>(_leaderboard.OnPersonaState);

            callbackManager.Subscribe<global::SteamKit2.SteamMatchmaking.GetLobbyListCallback>(_lobby.OnLobbyMatchList);
            callbackManager.Subscribe<global::SteamKit2.SteamFriends.PersonaStateCallback>(_lobby.OnPersonaState);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot) {
            return Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(null);
        }

        public Task OnBotSteamLevelsInit(Bot bot) => Task.CompletedTask;

        private async Task StartHttpServer() {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Config.Port}/");
            try {
                listener.Start();
                ASF.ArchiLogger.LogGenericInfo($"Interface runs on Http: http://127.0.0.1:{Config.Port}/");
            }
            catch (Exception ex) {
                ASF.ArchiLogger.LogGenericError($"HTTP Start failed: {ex.Message}");
                return;
            }

            while (true) {
                try {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(async () => await HandleRequest(ctx));
                }
                catch { await Task.Delay(1000); }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx) {
            string responseStr = "";
            int code = 200;
            string path = ctx.Request.Url.AbsolutePath;

            if (Config.Debug) {
                ASF.ArchiLogger.LogGenericInfo($"Request: {path}");
            }

            try {
                Bot bot = GetBot();
                int limit = 200;
                if (int.TryParse(ctx.Request.QueryString["count"], out int c)) limit = Math.Clamp(c, 1, 5000);

                if (path == "/GetLeaderboard") {
                    string lbName = ctx.Request.QueryString["name"] ?? Config.LeaderboardName;
                    if (string.IsNullOrEmpty(lbName)) {
                        lbName = Config.LeaderboardName;
                    }
                    responseStr = await _leaderboard.FetchXml(bot, limit, lbName ?? "Unknown");
                }
                else if (path == "/GetLobbies") {
                    string filterStr = ctx.Request.QueryString["filters"] ?? "";
                    var filtersToApply = new List<Dictionary<string, string>>();

                    if (filterStr.StartsWith("filters{") && filterStr.EndsWith("}")) {
                        string content = filterStr.Substring(8, filterStr.Length - 9);

                        // format ["k"="v"], name, ["k"="v"]
                        int idx = 0;
                        while (idx < content.Length) {
                            int openBracket = content.IndexOf('[', idx);
                            int comma = content.IndexOf(',', idx);

                            if (openBracket != -1 && (comma == -1 || openBracket < comma)) {
                                int closeBracket = content.IndexOf(']', openBracket);
                                if (closeBracket == -1) break;

                                string rawGroup = content.Substring(openBracket + 1, closeBracket - openBracket - 1);
                                var dict = new Dictionary<string, string>();

                                var pairs = rawGroup.Split(',', StringSplitOptions.RemoveEmptyEntries);
                                foreach (var p in pairs) {
                                    var parts = p.Split('=');
                                    if (parts.Length == 2) {
                                        string k = parts[0].Trim().Trim('"');
                                        string v = parts[1].Trim().Trim('"');
                                        dict[k] = v;
                                    }
                                }
                                filtersToApply.Add(dict);
                                idx = closeBracket + 1;
                            }
                            else {
                                int endOfToken = comma == -1 ? content.Length : comma;
                                string token = content.Substring(idx, endOfToken - idx).Trim();
                                if (!string.IsNullOrWhiteSpace(token)) {
                                    if (Config.PredefinedFilters.ContainsKey(token)) {
                                        filtersToApply.AddRange(Config.PredefinedFilters[token]);
                                    }
                                    else {
                                        ASF.ArchiLogger.LogGenericError($"Filter '{token}' not found in configuration.");
                                    }
                                }
                                idx = endOfToken;
                            }

                            if (idx < content.Length && content[idx] == ',') idx++;
                            while (idx < content.Length && char.IsWhiteSpace(content[idx])) idx++;
                        }
                    }

                    if (filtersToApply.Count == 0) {
                        filtersToApply.Add(new Dictionary<string, string>());
                    }

                    responseStr = await _lobby.GetLobbies(bot, limit, filtersToApply);
                }
                else {
                    code = 404;
                    responseStr = "<error>Unknown Endpoint</error>";
                }
            }
            catch (Exception ex) {
                code = 500;
                responseStr = $"<error>{ex.Message}</error>";
                if (Config.Debug) ASF.ArchiLogger.LogGenericError($"Request Error: {ex.Message}");
            }

            byte[] buf = Encoding.UTF8.GetBytes(responseStr);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "text/xml";
            ctx.Response.ContentLength64 = buf.Length;
            try { await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length); }
            catch { }
            finally { ctx.Response.Close(); }
        }

        private Bot GetBot() {
            if (_workingBot != null && _workingBot.IsConnectedAndLoggedOn) return _workingBot;

            var bots = ArchiSteamFarm.Steam.Bot.BotsReadOnly;
            if (bots != null) {
                var b = bots.Values.FirstOrDefault(x => x.IsConnectedAndLoggedOn);
                if (b != null) {
                    _workingBot = b;
                    return b;
                }
            }
            throw new Exception("No ASF Bot online.");
        }
    }
}
