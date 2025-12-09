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
    public class Leaderboard {
        private static ConcurrentQueue<TaskCompletionSource<global::SteamKit2.SteamUserStats.FindOrCreateLeaderboardCallback>> _findQueue
            = new ConcurrentQueue<TaskCompletionSource<global::SteamKit2.SteamUserStats.FindOrCreateLeaderboardCallback>>();

        private static ConcurrentQueue<TaskCompletionSource<global::SteamKit2.SteamUserStats.LeaderboardEntriesCallback>> _downloadQueue
            = new ConcurrentQueue<TaskCompletionSource<global::SteamKit2.SteamUserStats.LeaderboardEntriesCallback>>();

        private static ConcurrentDictionary<ulong, TaskCompletionSource<bool>> _pendingNames
            = new ConcurrentDictionary<ulong, TaskCompletionSource<bool>>();

        public void OnFindLeaderboard(global::SteamKit2.SteamUserStats.FindOrCreateLeaderboardCallback callback) {
            if (_findQueue.TryDequeue(out var tcs)) {
                tcs.TrySetResult(callback);
            }
        }

        public void OnLeaderboardScores(global::SteamKit2.SteamUserStats.LeaderboardEntriesCallback callback) {
            if (_downloadQueue.TryDequeue(out var tcs)) {
                tcs.TrySetResult(callback);
            }
        }

        public void OnPersonaState(global::SteamKit2.SteamFriends.PersonaStateCallback callback) {
            if (_pendingNames.TryRemove(callback.FriendID.ConvertToUInt64(), out var tcs)) {
                tcs.TrySetResult(true);
            }
        }

        public async Task<string> FetchXml(Bot bot, int max) {
            var stats = bot.GetHandler<SteamUserStats>();
            var friends = bot.GetHandler<SteamFriends>();

            if (stats == null || friends == null) throw new Exception("Steam Handlers missing.");

            var tcsFind = new TaskCompletionSource<global::SteamKit2.SteamUserStats.FindOrCreateLeaderboardCallback>();
            _findQueue.Enqueue(tcsFind);

            stats.FindLeaderboard(MainPlugin.Config.AppID, MainPlugin.Config.LeaderboardName);

            var findRes = await tcsFind.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (findRes.Result != EResult.OK) throw new Exception($"Leaderboard search failed: {findRes.Result}");
            if (findRes.ID == 0) throw new Exception($"Leaderboard '{MainPlugin.Config.LeaderboardName}' not found.");

            var tcsDl = new TaskCompletionSource<global::SteamKit2.SteamUserStats.LeaderboardEntriesCallback>();
            _downloadQueue.Enqueue(tcsDl);

            stats.GetLeaderboardEntries(MainPlugin.Config.AppID, findRes.ID, 1, max, ELeaderboardDataRequest.Global);

            var dlRes = await tcsDl.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (dlRes.Result != EResult.OK) throw new Exception($"Download failed: {dlRes.Result}");

            var unknownSteamIDs = new List<SteamID>();
            var waitTasks = new List<Task>();

            foreach (var entry in dlRes.Entries) {
                string cachedName = friends.GetFriendPersonaName(entry.SteamID);
                if (string.IsNullOrEmpty(cachedName) || cachedName == "[unknown]" || cachedName == entry.SteamID.Render()) {
                    unknownSteamIDs.Add(entry.SteamID);
                    var tcs = new TaskCompletionSource<bool>();
                    _pendingNames[entry.SteamID.ConvertToUInt64()] = tcs;
                    waitTasks.Add(tcs.Task);
                }
            }

            if (unknownSteamIDs.Count > 0) {
                friends.RequestFriendInfo(unknownSteamIDs, EClientPersonaStateFlag.PlayerName);
                try {
                    await Task.WhenAny(Task.WhenAll(waitTasks), Task.Delay(3000));
                }
                catch { }
            }

            XElement root = new XElement("response",
                new XElement("appID", MainPlugin.Config.AppID),
                new XElement("appFriendlyName", MainPlugin.Config.AppID),
                new XElement("leaderboardID", findRes.ID),
                new XElement("totalLeaderboardEntries", findRes.EntryCount),
                new XElement("entryStart", 0),
                new XElement("entryEnd", dlRes.Entries.Count),
                new XElement("resultCount", dlRes.Entries.Count)
            );

            XElement entriesNode = new XElement("entries");
            foreach (var e in dlRes.Entries) {
                StringBuilder detailsHex = new StringBuilder();
                if (e.Details != null) {
                    foreach (int val in e.Details) {
                        byte[] bytes = BitConverter.GetBytes(val);
                        foreach (byte b in bytes) detailsHex.Append(b.ToString("x2"));
                    }
                }

                string finalName = friends.GetFriendPersonaName(e.SteamID);
                ulong ugcId = (e.UGCId == ulong.MaxValue) ? 18446744073709551615 : e.UGCId.Value;

                entriesNode.Add(new XElement("entry",
                    new XAttribute("name", finalName),
                    new XElement("steamid", e.SteamID.ConvertToUInt64()),
                    new XElement("score", e.Score),
                    new XElement("rank", e.GlobalRank),
                    new XElement("ugcid", ugcId),
                    new XElement("name", finalName),
                    new XElement("details", new XCData(detailsHex.ToString()))
                ));
            }
            root.Add(entriesNode);

            foreach (var id in unknownSteamIDs) _pendingNames.TryRemove(id.ConvertToUInt64(), out _);

            return new XDocument(root).ToString();
        }
    }
}