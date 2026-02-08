# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, specifically optimized for Plex and NAS devices like the DS216+.

## Features
- **High Performance**: Built on .NET 10 Minimal APIs for near-zero idle CPU/RAM usage.
- **HDHomeRun Emulation**: Seamlessly discovered by Plex for Live TV & DVR.
- **Dynamic M3U/XMLTV**: Automatically generates playlists and guide data from your subscriptions.
- **Streamlink Powered**: Uses the gold standard for Twitch stream extraction.
- **Flexible YAML Support**: Works with both `twitch_recorder` (ytdl-sub) and `subscriptions` formats.
- **Nuclear Testing**: Includes a rigorous pre-build test suite (`test-nuclear.sh`).

---

## 🚀 Quick Start (Local)

Follow these atomic steps to get up and running locally.

### 1. Prerequisites
Ensure you have the following installed:
- **.NET SDK**: [NET 9.0 or higher](https://dotnet.microsoft.com/download)
- **Streamlink**: Required for fetching live streams.
  - **macOS**: `brew install streamlink`
  - **Linux/Windows**: `pip install streamlink`

### 2. Configuration
Create a `subscriptions.yaml` file in the project root:
```yaml
twitch_recorder:
  "Channel Name": "https://www.twitch.tv/channelname"
  "Another Channel": "https://www.twitch.tv/anotherchannel"
```

### 3. Verify Environment (Nuclear Test)
Run the included nuclear test script to verify dependencies, build the solution, and run unit tests.
```bash
chmod +x test-nuclear.sh
./test-nuclear.sh
```
*If this fails, follow the output instructions to fix missing dependencies.*

### 4. Run Application
Run the tuner. We recommend using port **5002** to avoid conflicts with macOS AirPlay (port 5000).

```bash
dotnet run --project TwitchPlexTuner.csproj --urls=http://localhost:5002
```

### 5. Connect Clients
- **Playlist (M3U)**: `http://localhost:5002/playlist.m3u`
- **Guide (XMLTV)**: `http://localhost:5002/epg.xml`
- **Stream**: `http://localhost:5002/stream/{channel_login}`

---

## 🐳 Docker Deployment

### 1. Create `compose.yaml`
```yaml
services:
  tpt:
    image: ghcr.io/oscaem/twitch-plex-tuner:main
    container_name: twitch-plex-tuner
    restart: always
    environment:
      - CLIENT_ID=your_client_id_here
      - CLIENT_SECRET=your_client_secret_here
      - BASE_URL=http://<YOUR_NAS_IP>:5200
      - SUBSCRIPTIONS_PATH=/config/subscriptions.yaml
    volumes:
      - /path/to/your/subscriptions.yaml:/config/subscriptions.yaml
    ports:
      - "5200:5000"

  threadfin:
    image: fyb3roptik/threadfin:latest
    container_name: threadfin
    restart: always
    ports:
      - "34400:34400"
    volumes:
      - ./threadfin/conf:/home/threadfin/conf
```

### 2. Run
```bash
docker-compose up -d
```

---

## Troubleshooting

### "Streamlink not found"
- Ensure `streamlink` is in your system PATH.
- Run `./test-nuclear.sh` to diagnose.

### "Address already in use"
- Port 5000 is often used by system services (Control Center on macOS).
- Use `--urls=http://localhost:5002` to bind to a different port.

### Empty EPG/Playlist
- Ensure `subscriptions.yaml` is formatted correctly.
- Check logs for "Loaded X channels".
