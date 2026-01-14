import requests
import xml.etree.ElementTree as ET
import sys

def get_leaderboard(count=200):
    url = f"http://127.0.0.1:12345/GetLeaderboard?count={count}"
    try:
        print(f"Requesting top {count} entries...")
        response = requests.get(url)
        if response.status_code == 200:
            return response.text
        else:
            print(f"Error: Status {response.status_code}")
            return None
    except requests.exceptions.ConnectionError:
        print("Error: Could not connect to ASF.")
        return None

def parse_and_print(xml_data):
    if not xml_data: return

    try:
        root = ET.fromstring(xml_data)
        app_id = root.find("appID").text
        total = root.find("totalLeaderboardEntries").text
        count = root.find("resultCount").text
        
        print("-" * 75)
        print(f"AppID: {app_id} | Total Global: {total} | Showing: {count}")
        print("-" * 75)
        print(f"{'RANK':<6} | {'SCORE':<10} | {'STEAMID':<18} | {'NAME'}")
        print("-" * 75)

        for entry in root.findall(".//entry"):
            rank = entry.find("rank").text
            score = entry.find("score").text
            steamid = entry.find("steamid").text
            name_node = entry.find("name")
            name = name_node.text if name_node is not None else "[unknown]"
            
            print(f"{rank:<6} | {score:<10} | {steamid:<18} | {name}")

    except ET.ParseError as e:
        print(f"XML Parse Error: {e}")
        print(xml_data)

if __name__ == "__main__":
    count_arg = 200
    if len(sys.argv) > 1:
        try:
            count_arg = int(sys.argv[1])
        except ValueError: pass

    xml_result = get_leaderboard(count_arg)
    print(xml_result) # dies kannste nutzen für den bot
    parse_and_print(xml_result)