# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, specifically optimized for Plex and NAS devices like the DS216+.

## Features
- **High Performance**: Built on .NET 10 Minimal APIs for near-zero idle CPU/RAM usage.
- **HDHomeRun Emulation**: Seamlessly discovered by Plex for Live TV & DVR.
- **Dynamic M3U/XMLTV**: Automatically generates playlists and guide data from your subscriptions.
- **Streamlink Powered**: Uses the gold standard for Twitch stream extraction.
- **Flexible YAML Support**: Works with both `twitch_recorder` (ytdl-sub) and `subscriptions` formats.

---

## Quick Start

### 1. Prerequisites
- **Twitch API Credentials**: Register an app at the [Twitch Dev Console](https://dev.twitch.tv/) to get your `CLIENT_ID` and `CLIENT_SECRET`.
- **Subscriptions File**: A `subscriptions.yaml` file containing your Twitch channels:

```yaml
twitch_recorder:
  "EdeLive": "https://www.twitch.tv/edelive"
  "Nils": "https://www.twitch.tv/nils"
  # Add more channels...
```

### 2. Docker Deployment

Create a `compose.yaml`:

```yaml
version: '3.8'

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
      - "5200:5000"  # External:Internal

  threadfin:
    image: fyb3roptik/threadfin:latest
    container_name: threadfin
    restart: always
    ports:
      - "34400:34400"
    volumes:
      - ./threadfin/conf:/home/threadfin/conf
```

### 3. Verify Endpoints

After starting the containers, test:
- **Playlist**: `http://<YOUR_NAS_IP>:5200/playlist.m3u`
- **EPG**: `http://<YOUR_NAS_IP>:5200/epg.xml`
- **Discovery**: `http://<YOUR_NAS_IP>:5200/discover.json`

### 4. Configure Threadfin

Access Threadfin at `http://<YOUR_NAS_IP>:34400`:
1. Add **M3U Source**: `http://twitch-plex-tuner:5000/playlist.m3u`
2. Add **XMLTV Source**: `http://twitch-plex-tuner:5000/epg.xml`
3. Enable buffer (recommended for Plex compatibility)

### 5. Add to Plex

1. Go to **Plex Settings** → **Live TV & DVR**
2. Add Device: `http://<YOUR_NAS_IP>:34400`
3. Scan channels and match guide data

---

## Troubleshooting

### Check Container Logs
```bash
docker logs -f twitch-plex-tuner
```

Look for:
- `=== UPDATE CHANNELS START ===`
- `Found X channels: ...`
- `=== UPDATE COMPLETE: X channels loaded ===`

### Empty Playlist/EPG
- Verify `CLIENT_ID` and `CLIENT_SECRET` are set correctly
- Check that `subscriptions.yaml` is mounted correctly
- Ensure the YAML structure uses `twitch_recorder:` or `subscriptions:` as the top-level key

---

## Development

### Local Build
```bash
dotnet build
```

### Docker Build
```bash
docker build -t twitch-plex-tuner .
```

### CI/CD
The included GitHub Actions workflow automatically builds and publishes to `ghcr.io` on every push to main.

---

## Architecture
- **TwitchPlexTuner**: C# application managing Twitch API, M3U/XMLTV generation, and HDHomeRun emulation
- **Streamlink**: Python-based stream extractor (bundled in Docker image)
- **Threadfin** (optional): Provides buffering for improved Plex compatibility
