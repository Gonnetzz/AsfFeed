using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using ArchiSteamFarm.Core;
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

        public async Task<string> GetLobbies(Bot bot, int limit, List<Dictionary<string, string>> filterSets) {
            var combinedLobbies = new List<SteamMatchmaking.Lobby>();
            var seenLobbyIDs = new HashSet<ulong>();

            foreach (var filterDict in filterSets) {
                try {
                    var lobbies = await FetchLobbiesInternal(bot, limit, filterDict);
                    foreach (var l in lobbies) {
                        if (seenLobbyIDs.Add(l.SteamID.ConvertToUInt64())) {
                            combinedLobbies.Add(l);
                        }
                    }
                }
                catch (Exception ex) {
                    ASF.ArchiLogger.LogGenericError($"Error fetching specific filter set: {ex.Message}");
                }
            }

            // sorted by steamid
            var sortedLobbies = combinedLobbies.OrderBy(x => x.SteamID.ConvertToUInt64()).ToList();

            return await ProcessNamesAndXml(bot, sortedLobbies);
        }

        private async Task<List<SteamMatchmaking.Lobby>> FetchLobbiesInternal(Bot bot, int limit, Dictionary<string, string> filtersDict) {
            var mm = bot.GetHandler<SteamMatchmaking>();
            if (mm == null) throw new Exception("SteamMatchmaking handler missing.");

            var tcs = new TaskCompletionSource<SteamMatchmaking.GetLobbyListCallback>();
            _lobbyQueue.Enqueue(tcs);

            var filters = new List<SteamMatchmaking.Lobby.Filter>();
            filters.Add(new SteamMatchmaking.Lobby.DistanceFilter(ELobbyDistanceFilter.Worldwide));

            foreach (var kvp in filtersDict) {
                filters.Add(new SteamMatchmaking.Lobby.StringFilter(kvp.Key, ELobbyComparison.Equal, kvp.Value));
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