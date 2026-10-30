# See https://docs.microsoft.com/aspnet/core/host-and-deploy/docker/building-net-docker-images
# for more information about using .NET with Docker.

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy main project file
COPY ["com.IvanMurzak.GameDev.MCP.Server.csproj", "."]

# Restore dependencies
RUN dotnet restore "com.IvanMurzak.GameDev.MCP.Server.csproj"

# Copy the rest of the source code
COPY . .

# Publish
RUN dotnet publish "com.IvanMurzak.GameDev.MCP.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Pre-create the writable logs dir so the non-root runtime user can write NLog
# output — the chiseled rootfs has no shell to mkdir at runtime.
RUN mkdir -p /app/publish/logs

# Runtime stage — chiseled (distroless) base: no shell, no apt, no perl/pam/tar,
# runs as a non-root user. This drops the debian-bookworm OS-package CVEs that the
# full aspnet:9.0 base carried. The *-extra variant still ships ICU + tzdata, so
# globalization keeps working with no application change.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled-extra AS final
WORKDIR /app
# The app user in the chiseled images is UID/GID 1654 (APP_UID). Own the app tree
# (including logs/) so the non-root process can write NLog files.
COPY --from=build --chown=1654:1654 /app/publish .

# MCP server metadata
LABEL io.modelcontextprotocol.server.name="io.github.IvanMurzak/GameDev-MCP-Server"

# Expose the default plugin port and the HTTP client port so external scanners
# (like Smithery) and platform port mappings can reach the server.
EXPOSE 8080

ENTRYPOINT ["dotnet", "gamedev-mcp-server.dll"]
