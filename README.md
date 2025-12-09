# AsfFeed
AsfFeed provides simple local HTTP endpoints for retrieving game leaderboard data and lobby information through an ASF plugin. 
The plugin must be running, and it requires a **settings.json** file placed in the same directory as the plugin.


## Configuration

Create a file named **settings.json** in the plugin directory. It must contain:

* `Port`: the HTTP port the plugin should listen on
* `AppID`: the game’s AppID
* `LeaderboardName`: the name of the leaderboard to query
* `Debug`: enables or disables debug output

Example:
```
{
  "Port": 12345,
  "AppID": 12345,
  "LeaderboardName": "LeaderboardName",
  "Debug": false
}
```

After configuration, the API is available at:

`http://127.0.0.1:PORT/`

---

## Endpoints

### **1. Get Leaderboard**

**Endpoint:**
`/GetLeaderboard`

**Parameters:**

* `count` – number of leaderboard entries to return (default: 200)

**Example usage:**
`http://127.0.0.1:12345/GetLeaderboard`
`http://127.0.0.1:12345/GetLeaderboard?count=200`

---

### **2. Get Lobbies**

**Endpoint:**
`/GetLobbies`

**Parameters:**

* `count` – number of lobbies to return
* `mode` – optional; supported values:

  * `normal` (default, max: 50)
  * `ranked_split` (only ranked/split lobbies, max: 100)

**Example usage:**
Normal mode:
`http://127.0.0.1:12345/GetLobbies`
`http://127.0.0.1:12345/GetLobbies?count=50`

Ranked split mode:
`http://127.0.0.1:12345/GetLobbies?mode=ranked_split`
`http://127.0.0.1:12345/GetLobbies?count=200&mode=ranked_split`

---

## Usage

You can call the endpoints directly in a browser or use them from scripts, bots, or tools like curl.