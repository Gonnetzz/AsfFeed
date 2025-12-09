using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace ASFLeaderboardPlugin {
    public class Lobby {
        private static ConcurrentQueue<TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>> _lobbyQueue
            = new ConcurrentQueue<TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>>();

        private static ConcurrentDictionary<ulong, TaskCompletionSource<bool>> _pendingNames
            = new ConcurrentDictionary<ulong, TaskCompletionSource<bool>>();

        public void OnLobbyMatchList(SteamMatchmaking.GetLobbyListCallback callback) {
            if (_lobbyQueue.TryDequeue(out var tcs)) {
                tcs.TrySetResult(callback);
            }
        }

        public void OnPersonaState(global::SteamKit2.SteamFriends.PersonaStateCallback callback) {
            if (_pendingNames.TryRemove(callback.FriendID.ConvertToUInt64(), out var tcs)) {
                tcs.TrySetResult(true);
            }
        }

        public async Task<string> GetLobbies(Bot bot, int limit, string? filterKey = null, string? filterVal = null) {
            var lobbies = await FetchLobbiesInternal(bot, limit, filterKey, filterVal);
            return await ProcessNamesAndXml(bot, lobbies);
        }

        public async Task<string> GetRankedAndUnrankedSplit(Bot bot, int limit) {
            var ranked = await FetchLobbiesInternal(bot, limit, "ranked", "1");
            var unranked = await FetchLobbiesInternal(bot, limit, "ranked", "0");

            var combined = new List<SteamMatchmaking.Lobby>();
            combined.AddRange(ranked);
            combined.AddRange(unranked);

            return await ProcessNamesAndXml(bot, combined);
        }

        private async Task<List<SteamMatchmaking.Lobby>> FetchLobbiesInternal(Bot bot, int limit, string? key, string? val) {
            var mm = bot.GetHandler<SteamMatchmaking>();
            if (mm == null) throw new Exception("SteamMatchmaking handler missing.");

            var tcs = new TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>();
            _lobbyQueue.Enqueue(tcs);

            var filters = new List<SteamMatchmaking.Lobby.Filter>();
            filters.Add(new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide));

            if (!string.IsNullOrEmpty(key) && val != null) {
                filters.Add(new SteamMatchmaking.Lobby.StringFilter(key, ELobbyComparison.Equal, val));
            }

            mm.GetLobbyList(MainPlugin.Config.AppID, filters, limit);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (result.Result != EResult.OK) throw new Exception($"Lobby fetch failed: {result.Result}");

            return result.Lobbies;
        }

        private async Task<string> ProcessNamesAndXml(Bot bot, List<SteamMatchmaking.Lobby> lobbies) {
            var friends = bot.GetHandler<SteamFriends>();
            if (friends != null) {
                var unknownOwners = new List<SteamID>();
                var waitTasks = new List<Task>();

                foreach (var l in lobbies) {
                    if (l.OwnerSteamID != null) {
                        string cachedName = friends.GetFriendPersonaName(l.OwnerSteamID);
                        if (string.IsNullOrEmpty(cachedName) || cachedName == "[unknown]" || cachedName == l.OwnerSteamID.Render()) {
                            unknownOwners.Add(l.OwnerSteamID);
                            var nameTcs = new TaskCompletionSource<bool>();
                            _pendingNames[l.OwnerSteamID.ConvertToUInt64()] = nameTcs;
                            waitTasks.Add(nameTcs.Task);
                        }
                    }
                }

                if (unknownOwners.Count > 0) {
                    friends.RequestFriendInfo(unknownOwners, EClientPersonaStateFlag.PlayerName);
                    try { await Task.WhenAny(Task.WhenAll(waitTasks), Task.Delay(3000)); } catch { }
                }
            }

            return GenerateXml(lobbies, friends);
        }

        private string GenerateXml(List<SteamMatchmaking.Lobby> lobbies, SteamFriends? friends) {
            XElement root = new XElement("response",
                new XElement("appID", MainPlugin.Config.AppID),
                new XElement("lobbyCount", lobbies.Count),
                new XElement("source", "LobbyList")
            );

            XElement listNode = new XElement("lobbies");
            foreach (var l in lobbies) {
                var lobbyNode = new XElement("lobby",
                    new XAttribute("id", l.SteamID.ConvertToUInt64())
                );

                lobbyNode.Add(new XElement("members", l.NumMembers));
                lobbyNode.Add(new XElement("max_members", l.MaxMembers));
                lobbyNode.Add(new XElement("type", l.LobbyType));

                if (l.OwnerSteamID != null) {
                    string ownerName = friends?.GetFriendPersonaName(l.OwnerSteamID) ?? "[Unknown]";
                    lobbyNode.Add(new XElement("owner", ownerName));
                    lobbyNode.Add(new XElement("ownerId", l.OwnerSteamID.ConvertToUInt64()));
                }

                foreach (var kvp in l.Metadata) {
                    lobbyNode.Add(new XElement(kvp.Key, kvp.Value));
                }

                listNode.Add(lobbyNode);
            }
            root.Add(listNode);
            foreach (var id in _pendingNames.Keys) _pendingNames.TryRemove(id, out _);

            return new XDocument(root).ToString();
        }
    }
}