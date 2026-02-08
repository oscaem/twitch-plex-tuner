# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, optimized for Plex and Synology Docker (NAS).

## Features
- **High Performance**: Built on .NET 10 Minimal APIs for near-zero idle CPU/RAM usage.
- **HDHomeRun Emulation**: Seamlessly discovered by Plex for Live TV & DVR.
- **Dynamic M3U/XMLTV**: Automatically generates playlists and guide data from your subscriptions.
- **Streamlink Powered**: Uses the gold standard for Twitch stream extraction.
- **Flexible YAML Support**: Works with both `twitch_recorder` (ytdl-sub) and `subscriptions` formats.
- **Nuclear Testing**: Includes a rigorous pre-build test suite (`test-nuclear.sh`).

---

## 🐳 Synology Docker Deployment

### 1. Prepare Configuration
Create a folder on your NAS (e.g., `/docker/twitch-plex-tuner`) and add a `subscriptions.yaml` file:
```yaml
twitch_recorder:
  "EdeLive": "https://www.twitch.tv/edelive"
  "Nils": "https://www.twitch.tv/nils"
```

### 2. Create `compose.yaml` (Synology)
Save this file in the same folder.
> **Note**: Replace `YOUR_NAS_IP` with your Synology's actual IP address (e.g., `192.168.1.100`).

```yaml
services:
  # The Antenna: Fetches Twitch streams and guide data
  tpt:
    image: ghcr.io/oscaem/twitch-plex-tuner:main
    container_name: twitch-plex-tuner
    restart: always
    environment:
      - CLIENT_ID=your_twitch_client_id
      - CLIENT_SECRET=your_twitch_client_secret
      # IMPORTANT: This URL is used in the M3U/XMLTV files.
      # It must be reachable by Threadfin.
      - BASE_URL=http://twitch-plex-tuner:5000 
      - SUBSCRIPTIONS_PATH=/config/subscriptions.yaml
    volumes:
      - ./subscriptions.yaml:/config/subscriptions.yaml
    ports:
      - "5200:5000" # Exposes tuner on port 5200 for debugging

  # The Manager: Buffers streams and presents them to Plex
  threadfin:
    image: fyb3roptik/threadfin:latest
    container_name: threadfin
    restart: always
    ports:
      - "34400:34400" # Web UI and Stream Port
    volumes:
      - ./threadfin/conf:/home/threadfin/conf
      - ./threadfin/temp:/home/threadfin/temp
```

### 3. Start Containers
Run via Container Manager or SSH:
```bash
docker-compose up -d
```

---

## ⚙️ Configuration Guide

### Step 1: Threadfin Setup
1.  Open Threadfin Web UI: `http://<YOUR_NAS_IP>:34400`
2.  **Settings** Tab:
    - **Tuner Count**: Set to `10` (or more).
    - **Stream Buffer**: Turn **ON** (Essential for Plex stability).
    - **EPG Source**: Set to **`XEPG`** (Crucial! Otherwise mapping won't work).
    - Save Settings.
3.  **Playlist** (M3U) Tab:
    - Click **New Playlist**.
    - **Type**: `M3U`
    - **Name**: `Twitch`
    - **URL**: `http://twitch-plex-tuner:5000/playlist.m3u` (Use container name)
    - **Tuner**: `10`
    - Save. You should see "X Channels".
4.  **XMLTV** Tab:
    - Click **New XMLTV File**.
    - **Source**: `HTTP`
    - **URL**: `http://twitch-plex-tuner:5000/epg.xml`
    - Save. You should see "X Programs".
5.  **Mapping** Tab:
    - Verify channels are listed. If names are missing, click the channel and manually assign the XMLTV ID.
    - *Note: Our recent fix ensures programs start 1 hour in the past so they appear "Live" immediately.*

### Step 2: Plex Setup
1.  Open Plex -> **Settings** (Wrench icon) -> **Live TV & DVR**.
2.  Click **Add DVR**.
3.  Do NOT select the discovered devices automatically (they might be the raw tuner).
4.  Click **"Enter its network address manually"**.
5.  Enter: `http://<YOUR_NAS_IP>:34400` (Threadfin's address).
6.  Click **Connect**.
7.  **Channel Mapping**:
    - Plex should see the channels.
    - Click **Continue**.
    - For "Guide Data", select **"XMLTV Guide"**.
    - Enter the XMLTV URL: `http://<YOUR_NAS_IP>:34400/xmltv.xml` (Threadfin generates this aggregated XML).
    - *Alternatively, you can re-use the tuner URL, but Threadfin's is often safer.*
8.  Complete the wizard.

### Step 3: View Stream
1.  Go to Plex **Live TV** section on the sidebar.
2.  You should see the "Guide" with your Twitch channels.
3.  Click a channel to play.
    - **Note**: If the channel is OFFLINE on Twitch, playback will fail (or show an error, depending on Plex client).
    - If the channel is LIVE, it should buffer briefly (Threadfin) and then play.

---

## 🩺 Troubleshooting

### "Playback Error" in Plex
- **Cause**: Channel might be offline.
- **Fix**: Check `twitch-plex-tuner` logs to see if `streamlink` found a stream.
- **Fix**: Ensure "Stream Buffer" is ON in Threadfin. Docker networking can be flaky without it.

### "No Channels" in Threadfin
- **Cause**: Tuner container not reachable.
- **Fix**: Ensure both services are in the same `compose.yaml` (bridge network).
- **Check**: Run `curl http://twitch-plex-tuner:5000/playlist.m3u` from *inside* the Threadfin container.

### "Guide is Empty" in Plex
- **Cause**: XMLTV start times are in the future (fixed) OR Threadfin hasn't enabled the channels (Common!).
- **Check (Threadfin)**: Go to **Filter** tab.
    - Are your channels listed?
    - **CRITICAL**: Do they have a **Green Sidebar** (Active)?
    - If not, click the channel and ensure "Active" is checked (or use Bulk Edit -> Active).
- **Check (Output)**: Output URL MUST be `http://<NAS_IP>:34400/xmltv.xml`. Any other URL (like `/` or `/device.xml`) serves UPnP data.
- **Fix**: Force refresh Guide Data in Plex Settings.
