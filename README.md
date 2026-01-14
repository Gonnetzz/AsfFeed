# AsfFeed
AsfFeed provides simple local HTTP endpoints for retrieving game leaderboard data and lobby information through an ASF plugin. 
The plugin must be running, and it requires a **settings.json** file placed in the same directory as the plugin.


## Configuration

Create a file named **settings.json** in the plugin directory. It must contain:

* `Port`: the HTTP port the plugin should listen on
* `AppID`: the game’s AppID
* `LeaderboardName`: the name of the leaderboard to query
* `Debug`: enables or disables debug output
* `PredefinedFilters`: optional dictionary to define filter aliases (e.g. "test")

Example:
```json
{
  "Port": 12345,
  "AppID": 12345,
  "LeaderboardName": "LeaderboardName",
  "Debug": false,
  "PredefinedFilters": {
    "test": [
      {
        "ranked": "1",
        "type": "1"
      },
      {
        "ranked": "1",
        "type": "2"
      }
    ]
  }
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

* `filters` – optional; a formatted string defining search filters. The format is `filters{...}`. Inside the braces, you can list multiple comma-separated filters. A filter can be:
    * A predefined name from `settings.json` (e.g. `test`).
    * A specific key-value pair in brackets: `["key"="value"]`.
  
  The plugin will make a separate Steam request for each filter group defined and merge the results (deduplicated by Lobby ID).

**Example usage:**

Unfiltered (returns default batch):
`http://127.0.0.1:12345/GetLobbies`

Using a predefined filter named "test":
`http://127.0.0.1:12345/GetLobbies?filters=filters{test}`

Using a direct filter for ranked lobbies:
`http://127.0.0.1:12345/GetLobbies?filters=filters{["ranked"="1"]}`

Combining a predefined filter and a direct filter:
`http://127.0.0.1:12345/GetLobbies?filters=filters{test, ["type"="1"]}`

---

## Usage

You can call the endpoints directly in a browser or use them from scripts, bots, or tools like curl.