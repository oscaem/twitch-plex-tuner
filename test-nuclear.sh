#!/bin/bash
set -e

echo "☢️  INITIATING NUCLEAR TEST SEQUENCE ☢️"

# 1. Clean verify
echo "🧹 Cleaning solution..."
dotnet clean
rm -rf bin obj TwitchPlexTuner.Tests/bin TwitchPlexTuner.Tests/obj

# 2. Check dependencies
echo "🔍 Checking dependencies..."
if ! command -v streamlink &> /dev/null; then
    echo "❌ streamlink could not be found"
    echo "   👉 Please install it via: brew install streamlink"
    echo "   👉 Or: pip install streamlink"
    exit 1
fi
echo "✅ streamlink found"

# 3. Environment Check
echo "🔍 Checking environment..."
if [ ! -f "appsettings.json" ]; then
    echo "❌ appsettings.json missing"
    exit 1
fi
echo "✅ appsettings.json found"

# 4. Run Tests
echo "🧪 Running Unit Tests..."
dotnet test TwitchPlexTuner.Tests/TwitchPlexTuner.Tests.csproj --verbosity normal

# 5. Build
echo "🏗️  Building Project..."
dotnet build TwitchPlexTuner.csproj -c Release

echo "✅ NUCLEAR TEST PASSED. SYSTEM READY FOR DEPLOYMENT."
