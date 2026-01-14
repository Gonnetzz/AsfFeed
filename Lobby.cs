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
            ulong sid = callback.FriendID.ConvertToUInt64();
            if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] OnPersonaState Callback: ID={sid}, Name='{callback.Name}', State={callback.State}");

            if (_pendingNames.TryRemove(sid, out var tcs)) {
                if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] -> Task matched and set for {sid}");
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
                var batchIds = new HashSet<ulong>();

                if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] Processing {lobbies.Count} lobbies for names...");

                foreach (var l in lobbies) {
                    ulong lobbyId = l.SteamID.ConvertToUInt64();

                    SteamID? ownerSteamID = l.OwnerSteamID;

                    if (ownerSteamID == null && l.Metadata.TryGetValue("ownerId", out string? ownerIdStr)) {
                        if (ulong.TryParse(ownerIdStr, out ulong parsedId)) {
                            ownerSteamID = new SteamID(parsedId);
                            if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] Lobby {lobbyId}: Found ownerId in metadata: {parsedId}");
                        }
                    }

                    if (ownerSteamID != null) {
                        string? cachedName = friends.GetFriendPersonaName(ownerSteamID);
                        ulong sid = ownerSteamID.ConvertToUInt64();

                        if (MainPlugin.Config.Debug) {
                            ASF.ArchiLogger.LogGenericInfo($"[DEBUG] Lobby {lobbyId}: Checking Owner {sid}. CachedName: '{cachedName}'");
                        }

                        if ((string.IsNullOrEmpty(cachedName) || cachedName == "[unknown]" || cachedName == ownerSteamID.Render())
                             && batchIds.Add(sid)) {

                            var tcs = _pendingNames.GetOrAdd(sid, _ => {
                                unknownOwners.Add(ownerSteamID);
                                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            });
                            waitTasks.Add(tcs.Task);
                        }
                    }
                }

                if (unknownOwners.Count > 0) {
                    if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] Requesting info for {unknownOwners.Count} users...");
                    friends.RequestFriendInfo(unknownOwners, EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence);
                }

                if (waitTasks.Count > 0) {
                    try {
                        if (MainPlugin.Config.Debug) ASF.ArchiLogger.LogGenericInfo($"[DEBUG] Waiting for {waitTasks.Count} tasks...");
                        await Task.WhenAny(Task.WhenAll(waitTasks), Task.Delay(5000));
                    }
                    catch { }

                    foreach (var id in batchIds) {
                        _pendingNames.TryRemove(id, out _);
                    }
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

                string ownerName = "[Unknown]";

                SteamID? effectiveOwnerId = l.OwnerSteamID;
                if (effectiveOwnerId == null && l.Metadata.TryGetValue("ownerId", out string? ownerIdStr) && ulong.TryParse(ownerIdStr, out ulong parsed)) {
                    effectiveOwnerId = new SteamID(parsed);
                }

                if (l.Metadata.TryGetValue("owner", out string? metaName) && !string.IsNullOrWhiteSpace(metaName)) {
                    ownerName = metaName;
                }

                else if (effectiveOwnerId != null && friends != null) {
                    var friendName = friends.GetFriendPersonaName(effectiveOwnerId);
                    if (!string.IsNullOrEmpty(friendName) && friendName != "[unknown]") {
                        ownerName = friendName;
                    }
                }

                lobbyNode.Add(new XElement("owner", ownerName));

                if (l.OwnerSteamID != null) {
                    lobbyNode.Add(new XElement("ownerId", l.OwnerSteamID.ConvertToUInt64()));
                }

                foreach (var kvp in l.Metadata) {
                    lobbyNode.Add(new XElement(kvp.Key, kvp.Value ?? ""));
                }

                listNode.Add(lobbyNode);
            }
            root.Add(listNode);

            return new XDocument(root).ToString();
        }
    }
}