import requests
import xml.etree.ElementTree as ET
import sys

def get_lobbies(count=200, mode="normal"):
    params = f"count={count}"
    if mode == "split":
        params += "&mode=ranked_split"
    
    url = f"http://127.0.0.1:12345/GetLobbies?{params}"
    
    try:
        print(f"Requesting lobbies (Mode: {mode})...")
        response = requests.get(url)
        
        if response.status_code == 200:
            return response.text
        else:
            print(f"Error: Status {response.status_code}")
            return None
            
    except requests.exceptions.ConnectionError:
        print("Error: Could not connect to ASF Plugin.")
        return None

def parse_and_print_lobbies(xml_data):
    if not xml_data: return

    try:
        root = ET.fromstring(xml_data)
        
        if root.tag == "error":
            print(f"Server Error: {root.text}")
            return

        app_id = root.find("appID").text
        count = root.find("lobbyCount").text
        
        print("-" * 100)
        print(f"AppID: {app_id} | Total Lobbies Found: {count}")
        print("-" * 100)
        print(f"{'LOBBY ID':<20} | {'MEM':<3}/{'MAX':<3} | {'OWNER':<20} | {'NAME / MAP'}")
        print("-" * 100)

        for lobby in root.findall(".//lobbies/lobby"):
            lid = lobby.get("id")
            members = lobby.find("members").text
            max_m = lobby.find("max_members").text
            
            owner_node = lobby.find("owner")
            owner = owner_node.text if owner_node is not None else "???"
            
            name_node = lobby.find("name")
            name = name_node.text if name_node is not None else ""
            
            map_node = lobby.find("Map")
            map_name = map_node.text if map_node is not None else ""

            ranked_node = lobby.find("ranked")
            is_ranked = " [R]" if ranked_node is not None and ranked_node.text == "1" else ""
            
            display_info = f"{name} {map_name}{is_ranked}"
            
            print(f"{lid:<20} | {members:<3}/{max_m:<3} | {owner:<20} | {display_info}")

    except ET.ParseError as e:
        print(f"XML Parse Error: {e}")
        # print(xml_data)

if __name__ == "__main__":
    mode_arg = "normal"
    if len(sys.argv) > 1 and sys.argv[1] == "split":
        mode_arg = "split"

    xml_result = get_lobbies(200, mode_arg)
    parse_and_print_lobbies(xml_result)