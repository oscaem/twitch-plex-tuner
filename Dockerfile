FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY ["TwitchPlexTuner.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Install Python and Streamlink
RUN apk add --no-cache python3 py3-pip && \
    pip install --break-system-packages streamlink

# Environment Variables Defaults
ENV CLIENT_ID=""
ENV CLIENT_SECRET=""
ENV SUBSCRIPTIONS_PATH="/config/subscriptions.yaml"
ENV BASE_URL="http://localhost:5000"
ENV ASPNETCORE_URLS="http://+:5000"

EXPOSE 5000

ENTRYPOINT ["dotnet", "TwitchPlexTuner.dll"]
