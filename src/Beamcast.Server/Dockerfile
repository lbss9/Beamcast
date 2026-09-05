# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Beamcast/Logic/SafeTry.cs src/Beamcast/Logic/
COPY src/Beamcast/Net/Protocol.cs src/Beamcast/Net/LoungeProtocol.cs src/Beamcast/Net/LoungeCrypto.cs src/Beamcast/Net/JoinRateLimiter.cs src/Beamcast/Net/FrameGate.cs src/Beamcast/Net/Messages.cs src/Beamcast/Net/
COPY src/Beamcast.Server/ src/Beamcast.Server/
RUN dotnet publish src/Beamcast.Server/Beamcast.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV BEAMCAST_DATA=/data
VOLUME ["/data"]
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Beamcast.Server.dll"]
