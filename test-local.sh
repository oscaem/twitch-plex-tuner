#!/bin/bash

echo "=== Testing twitch-plex-tuner locally ==="

# Set environment variables
export CLIENT_ID="o39ng3i2xcvx2vnqdhhap29yyh21au"
export CLIENT_SECRET="owwmuiyseelgp32yuy8bid7002kv41"
export SUBSCRIPTIONS_PATH="/Volumes/docker/twitch2tuner-fork/ytdl-sub/config/subscriptions.yaml"
export BASE_URL="http://localhost:5000"

echo "Environment:"
echo "  CLIENT_ID: $CLIENT_ID"
echo "  SUBSCRIPTIONS_PATH: $SUBSCRIPTIONS_PATH"
echo ""

# Check if subscriptions file exists
if [ -f "$SUBSCRIPTIONS_PATH" ]; then
    echo "✓ Subscriptions file exists"
    echo "  Content preview:"
    head -n 5 "$SUBSCRIPTIONS_PATH" | sed 's/^/    /'
else
    echo "✗ Subscriptions file NOT FOUND at $SUBSCRIPTIONS_PATH"
fi

echo ""
echo "Starting application..."
dotnet run
