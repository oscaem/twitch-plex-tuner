#!/bin/bash

echo "=== Local Testing for twitch-plex-tuner ==="
echo ""

# Configuration
export CLIENT_ID="o39ng3i2xcvx2vnqdhhap29yyh21au"
export CLIENT_SECRET="owwmuiyseelgp32yuy8bid7002kv41"
export SUBSCRIPTIONS_PATH="/Volumes/docker/twitch-plex-tuner/ytdl-sub/config/subscriptions.yaml"
export BASE_URL="http://localhost:5000"
export ASPNETCORE_URLS="http://localhost:5000"

# Check subscriptions file
if [ ! -f "$SUBSCRIPTIONS_PATH" ]; then
    echo "ERROR: Subscriptions file not found at $SUBSCRIPTIONS_PATH"
    exit 1
fi

echo "✓ Configuration validated"
echo "  CLIENT_ID: ${CLIENT_ID:0:10}..."
echo "  SUBSCRIPTIONS_PATH: $SUBSCRIPTIONS_PATH"
echo ""

# Build the project
echo "Building project..."
dotnet build -c Release
if [ $? -ne 0 ]; then
    echo "ERROR: Build failed"
    exit 1
fi

echo "✓ Build successful"
echo ""

# Start the application in background
echo "Starting application on port 5000..."
dotnet run &
APP_PID=$!

# Wait for app to start
echo "Waiting for app to initialize..."
sleep 5

# Test endpoints
echo ""
echo "=== Testing Endpoints ==="

test_endpoint() {
    local url=$1
    local name=$2
    echo -n "Testing $name... "
    
    response=$(curl -s -o /dev/null -w "%{http_code}" "$url")
    
    if [ "$response" = "200" ]; then
        echo "✓ OK ($response)"
        # Show first few lines of response
        curl -s "$url" | head -n 5
        echo ""
    else
        echo "✗ FAILED ($response)"
    fi
}

test_endpoint "http://localhost:5000/discover.json" "Discovery"
test_endpoint "http://localhost:5000/lineup.json" "Lineup"
test_endpoint "http://localhost:5000/playlist.m3u" "Playlist"
test_endpoint "http://localhost:5000/epg.xml" "EPG"

echo ""
echo "=== Testing Complete ==="
echo ""
echo "Press Ctrl+C to stop the application"

# Wait for user to stop
wait $APP_PID
