# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, optimized for Plex and Synology Docker (NAS).

## Features
- **High Performance**: Built on .NET 10 Minimal APIs for near-zero idle CPU/RAM usage.
- **HDHomeRun Emulation**: Seamlessly discovered by Plex for Live TV & DVR.
- **Dynamic M3U/XMLTV**: Automatically generates playlists and guide data from your subscriptions.
- **Dual Streaming Backend**: Uses Streamlink for URL discovery + yt-dlp for stable streaming.
- **Integrated Recording**: Auto-records live streams without Plex Pass (optional).
- **URL Caching**: Avoids re-discovery for faster channel switching.
- **Flexible YAML Support**: Works with both `twitch_recorder` (ytdl-sub) and `subscriptions` formats.

---

## 🐳 Synology Docker Deployment

### 1. Prepare Configuration

Create a folder on your NAS (e.g., `/volume1/docker/twitch-plex-tuner`) with:

**`subscriptions.yaml`**:
```yaml
twitch_recorder:
  "EdeLive": "https://www.twitch.tv/edelive"
  "Nils": "https://www.twitch.tv/nils"
```

**`.env`** (create this file):
```bash
CLIENT_ID=your_twitch_client_id
CLIENT_SECRET=your_twitch_client_secret
SUBSCRIPTIONS_PATH=./subscriptions.yaml
RECORDING_PATH=/volume1/Media/Twitch
```

### 2. Create `compose.yaml`

```yaml
services:
  tpt:
    image: ghcr.io/oscaem/twitch-plex-tuner:main
    container_name: twitch-plex-tuner
    restart: always
    environment:
      - CLIENT_ID=${CLIENT_ID}
      - CLIENT_SECRET=${CLIENT_SECRET}
      - BASE_URL=http://twitch-plex-tuner:5000
      - STREAM_QUALITY=1080p60,1080p,720p60,720p,best
      - RECORDING_PATH=/recordings  # Set to enable recording
    volumes:
      - ./subscriptions.yaml:/config/subscriptions.yaml:ro
      - ${RECORDING_PATH}:/recordings
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
      - ./threadfin/temp:/tmp/threadfin
```

### 3. Start Containers
```bash
docker-compose up -d
```

---

## ⚙️ Configuration Guide

### Threadfin Setup
1. Open Threadfin: `http://<NAS_IP>:34400`
2. **Settings**:
   - Tuner Count: `10`
   - **Buffer**: `4 MB` (important for DS216+)
   - **Timeout**: `5000ms`
   - EPG Source: `XEPG`
3. **Playlist**: 
   - URL: `http://twitch-plex-tuner:5000/playlist.m3u`
4. **XMLTV**:
   - URL: `http://twitch-plex-tuner:5000/epg.xml`

### Plex Setup
1. Settings → Live TV & DVR → Add DVR
2. Enter: `http://<NAS_IP>:34400` (Threadfin)
3. For guide: `http://<NAS_IP>:34400/xmltv.xml`

---

## 🔄 Synology Rebuild Workflow

**Problem**: Container Manager doesn't properly rebuild images.

**Solution**: SSH into NAS and use `rebuild.sh`:

```bash
cd /volume1/docker/twitch-plex-tuner

# Normal rebuild (preserves Threadfin config)
./scripts/rebuild.sh

# Full reset (clears Threadfin too)
./scripts/rebuild.sh --clean
```

**Manual Commands**:
| Action | Command |
|--------|---------|
| View logs | `docker-compose logs -f tpt` |
| Restart tuner | `docker-compose restart tpt` |
| Force pull | `docker-compose pull` |
| Rebuild one service | `docker-compose build --no-cache tpt` |

---

## 📹 Recording (No Plex Pass Needed)

When `RECORDING_PATH` is set, the tuner automatically records live streams:

- Checks for live channels every 2 minutes
- Saves to: `{RECORDING_PATH}/{DisplayName}/{timestamp} - {title}.ts`
- Stops recording when stream ends
- Files organized by streamer name

**To disable recording**: Remove the `RECORDING_PATH` environment variable.

**To change quality**: Set `STREAM_QUALITY=720p60,720p,best` (lower for NAS storage).

---

## 🛠️ Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CLIENT_ID` | required | Twitch API Client ID |
| `CLIENT_SECRET` | required | Twitch API Client Secret |
| `BASE_URL` | `http://localhost:5000` | Public URL for M3U/XMLTV |
| `STREAM_QUALITY` | `1080p60,1080p,720p60,720p,best` | Quality preference |
| `RECORDING_PATH` | *(disabled)* | Path to save recordings |
| `GUIDE_UPDATE_MINUTES` | `10` | How often to refresh stream status |

---

## 🩺 Troubleshooting

### Stream won't play
1. Check if channel is actually live on Twitch
2. View logs: `docker-compose logs -f tpt`
3. Ensure Threadfin buffer is ON

### "Building" doesn't update image
Use `./scripts/rebuild.sh` instead of Container Manager UI.

### Recording not working
1. Verify `RECORDING_PATH` is set and mounted
2. Check volume permissions (PUID/PGID)
3. View logs for recording errors

### Guide shows wrong status
Guide updates every 10 minutes. For fastest updates, manually refresh in Plex: Settings → Live TV → Refresh Guide.
