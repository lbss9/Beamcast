# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Beamcast/Logic/SafeTry.cs src/Beamcast/Logic/
COPY src/Beamcast/Net/Protocol.cs src/Beamcast/Net/RelayProtocol.cs src/Beamcast/Net/FrameGate.cs src/Beamcast/Net/Messages.cs src/Beamcast/Net/
COPY src/Beamcast.Relay/ src/Beamcast.Relay/
RUN dotnet publish src/Beamcast.Relay/Beamcast.Relay.csproj -c Release -o /app/publish

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Beamcast.Relay.dll"]
