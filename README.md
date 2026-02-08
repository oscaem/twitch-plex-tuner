# twitch-plex-tuner

A lightweight C# DVR Tuner and IPTV Proxy for Twitch, specifically optimized for Plex and NAS devices like the DS216+.

## Features
- **High Performance**: Built on .NET 9 Minimal APIs for near-zero idle CPU/RAM usage.
- **HDHomeRun Emulation**: Seamlessly discovered by Plex for Live TV & DVR.
- **Dynamic M3U/XMLTV**: Automatically generates playlists and guide data from your subscriptions.
- **Threadfin Integration**: Built-in compatibility with Threadfin for robust stream buffering and signal linearity.
- **Streamlink Powered**: Uses the gold standard for Twitch stream extraction.

---

## Deployment Guide (DS216+ / Docker)

### 1. Prerequisites
- **Twitch API Credentials**: Register an app at the [Twitch Dev Console](https://dev.twitch.tv/) to get your `CLIENT_ID` and `CLIENT_SECRET`.
- **Subscriptions File**: A `subscriptions.yaml` file (compatible with `ytdl-sub`) containing the list of channels you want to tune into.

### 2. Configuration (`compose.yaml`)
Create or update your `compose.yaml` with the following services:

```yaml
version: '3.8'

services:
  tpt:
    image: ghcr.io/<your-github-username>/twitch-plex-tuner:latest
    container_name: twitch-plex-tuner
    restart: always
    environment:
      - CLIENT_ID=your_client_id
      - CLIENT_SECRET=your_client_secret
      - BASE_URL=http://<DS216_IP>:5000
      - SUBSCRIPTIONS_PATH=/config/subscriptions.yaml
    volumes:
      - /volume1/docker/ytdl-sub/config/subscriptions.yaml:/config/subscriptions.yaml
    ports:
      - "5000:5000"

  threadfin:
    image: fuzzymistborn/threadfin:latest
    container_name: threadfin
    restart: always
    ports:
      - "34400:34400"
    volumes:
      - ./threadfin/conf:/home/threadfin/conf
```

### 3. Setup in Threadfin
1.  Access Threadfin at `http://<DS216_IP>:34400`.
2.  **M3U Source**: Add a new M3U source pointing to `http://twitch-plex-tuner:5000/playlist.m3u`.
3.  **XMLTV Source**: Add a new XMLTV source pointing to `http://twitch-plex-tuner:5000/epg.xml`.
4.  **Buffer**: Ensure Threadfin is set to use its internal buffer (default) for best compatibility with Plex.

### 4. Integration with Plex
1.  Go to **Plex Settings** -> **Live TV & DVR**.
2.  Click **Add Device**.
3.  Enter the address of Threadfin: `http://<DS216_IP>:34400`.
4.  Plex will scan the "channels" provided by `twitch-plex-tuner` via Threadfin.
5.  Match the channels to the XMLTV guide data if prompted.

---

## Architecture
- **TwitchPlexTuner**: The management layer. It handles the Twitch API, keeps track of live status, and serves the M3U/XMLTV files.
- **Threadfin**: The signal proxy. It makes the irregular Twitch stream appear as a constant, linear signal to Plex.
- **Streamlink**: The heavy-lifter. It extracts the raw video data from Twitch on demand.

---

## Development & CI/CD
This repository includes a GitHub Action that automatically builds and publishes a Docker image to GitHub Packages (`ghcr.io`) on every commit to the main branch.

To build manually:
```bash
docker build -t twitch-plex-tuner .
```
