#!/bin/bash
# rebuild.sh - Rebuild twitch-plex-tuner without losing Threadfin settings
#
# Usage: ./scripts/rebuild.sh [--clean]
#   --clean: Also reset Threadfin configuration (nuclear option)

set -e

cd "$(dirname "$0")/.."

echo "🔄 Rebuilding twitch-plex-tuner..."

# Parse arguments
CLEAN=false
if [[ "$1" == "--clean" ]]; then
    CLEAN=true
    echo "⚠️  Clean mode: Threadfin config will be reset"
fi

# 1. Stop containers (preserves volumes)
echo "📦 Stopping containers..."
docker-compose down

# 2. Remove ONLY the tuner image (keep threadfin to preserve config)
echo "🗑️  Removing tuner image..."
docker rmi twitch-plex-tuner:latest 2>/dev/null || true

# 3. Clean Threadfin if requested
if [ "$CLEAN" = true ]; then
    echo "🧹 Cleaning Threadfin configuration..."
    rm -rf ./threadfin/conf/*
fi

# 4. Force rebuild with no cache
echo "🏗️  Building tuner (no cache)..."
docker-compose build --no-cache tpt

# 5. Pull latest threadfin image
echo "⬇️  Pulling latest Threadfin..."
docker-compose pull threadfin

# 6. Start fresh
echo "🚀 Starting containers..."
docker-compose up -d

echo ""
echo "✅ Rebuild complete!"
echo ""
echo "📊 Container status:"
docker-compose ps

echo ""
echo "📝 View logs: docker-compose logs -f tpt"
echo "🌐 Tuner: http://localhost:5200"
echo "🌐 Threadfin: http://localhost:34400"
