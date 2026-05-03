# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, optimized for Plex and Synology Docker (NAS).

HEAVILY inspired by https://github.com/micahmo/twitch2tuner.

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

# Optional: only these channels will be recorded when RECORDING_PATH is set
recording:
  - edelive
  - nils
```

**`.env`** (create this file):
```bash
CLIENT_ID=your_twitch_client_id
CLIENT_SECRET=your_twitch_client_secret
SUBSCRIPTIONS_PATH=./subscriptions.yaml
RECORDING_PATH=/recordings          # Use the container path here!
```

> [!CAUTION]
> **RECORDING_PATH** must match the **internal container path** defined in your volumes (e.g., `/recordings`). Do NOT use the host path (like `/volume1/...`) in the environment variable. If you do, files will be saved inside the container and lost when it restarts.

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

Recording uses **streamlink** (separate from the live streaming pipeline) and only records channels listed in the `recording:` section of `subscriptions.yaml`.

- Checks for live channels every 2 minutes
- Saves to: `{RECORDING_PATH}/{DisplayName}/{timestamp} - {title}.ts`
- Stops recording when stream ends
- Keeps recordings as `.ts`; no MP4 remux/conversion is performed
- Files organized by streamer name
- **Retention**: Old recordings are deleted after `RECORDING_RETENTION_DAYS` (default 30 days)

### Per-Channel Config
Add a `recording:` list to your `subscriptions.yaml` with the login names of channels to record:
```yaml
recording:
  - edelive
  - nils
```
Channels **not** in this list are tracked/viewable but **not** recorded.

**To disable all recording**: Remove the `RECORDING_PATH` environment variable.

---

## 🛠️ Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CLIENT_ID` | required | Twitch API Client ID |
| `CLIENT_SECRET` | required | Twitch API Client Secret |
| `BASE_URL` | `http://localhost:5000` | Public URL for M3U/XMLTV |
| `STREAM_QUALITY` | `1080p60,1080p,720p60,720p,best` | Quality preference |
| `TWITCH_OAUTH_TOKEN` | *(empty)* | Optional Twitch website auth token for Streamlink. Useful when the account has Twitch Turbo or a channel subscription that Twitch honors for ad-free playback. Keep this secret. |
| `RECORDING_PATH` | *(disabled)* | Internal container path to save recordings (e.g. `/recordings`) |
| `RECORDING_RETENTION_DAYS` | `30` | Days to keep recordings before deletion |
| `GUIDE_UPDATE_MINUTES` | `2` | How often to refresh Twitch stream status and trigger Jellyfin guide refresh. Values below 2 are clamped to 2 minutes to avoid hammering Twitch/Jellyfin. |

### Twitch Ads

The app asks Streamlink to filter Twitch embedded ads for both live playback and recordings. On recent Streamlink releases, this filtering is automatic; the old `--twitch-disable-ads` option is still included for older versions.

Filtering does **not** recover the hidden live content. During Twitch preroll or midroll ads, Streamlink pauses output until regular content returns, which can show up as a gap or time skip in a live view or recording. Fully preventing ads depends on Twitch returning an ad-free stream to the account, for example Twitch Turbo or a subscribed channel. You can pass that account's Twitch website OAuth token with `TWITCH_OAUTH_TOKEN`.

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
Guide updates every `GUIDE_UPDATE_MINUTES` (default 2). Jellyfin re-fetches M3U/XMLTV automatically; ensure your Jellyfin Live TV guide refresh is set to match. Playlist/EPG requests also opportunistically refresh Twitch status when the cached state is older than 2 minutes. For Plex: Settings → Live TV → Refresh Guide.

---

## 🙏 Acknowledgements

This project is heavily inspired by and based on the excellent work of **twitch2tuner**.
- Original Project: [twitch2tuner](https://github.com/micahmo/twitch2tuner) by micahmo
- License: MIT

We have extended the original concept with `.NET 10`, `yt-dlp` stability improvements, and enhanced recording capabilities.
