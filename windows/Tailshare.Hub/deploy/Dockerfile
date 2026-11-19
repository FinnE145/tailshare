# Build context is windows/ (see compose.yaml): docker build -f Tailshare.Hub/deploy/Dockerfile windows
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Tailshare.Hub/Tailshare.Hub.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Not root: the hub only reads Tailscale's socket and writes /data. The compose file's data
# directory has to belong to this id.
USER 2003:2003
ENTRYPOINT ["dotnet", "Tailshare.Hub.dll", "--data", "/data"]
