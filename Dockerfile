FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["TwitchPlexTuner.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Install Python, Streamlink (for URL discovery), yt-dlp (for streaming), and ffmpeg
RUN apk add --no-cache python3 py3-pip ffmpeg && \
    pip install --break-system-packages streamlink yt-dlp

# Environment Variables Defaults
ENV SUBSCRIPTIONS_PATH="/config/subscriptions.yaml"
ENV BASE_URL="http://localhost:5000"
ENV ASPNETCORE_URLS="http://+:5000"
ENV STREAM_QUALITY="1080p60,1080p,720p60,720p,best"
ENV RECORDING_PATH="/recordings"
ENV GUIDE_UPDATE_MINUTES="5"

EXPOSE 5000

ENTRYPOINT ["dotnet", "TwitchPlexTuner.dll"]

