FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# 8080: the Portal. 8081: VS Code, which needs an origin of its own (see EDITOR_PORT in README.md).
EXPOSE 8080 8081
# /data holds the database, the encryption keys and the Manager's SSH key. Owned by the non-root "app" user,
# so a named volume mounted there starts with the right permissions.
RUN mkdir -p /data && chown app:app /data
ENV DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080;8081 \
    CPU_TRACE_STARTUP=true \
    DOTNET_EventPipeThreadSamplingRate=0x32
# CoreCLR reads this hexadecimal DWORD before Main: 0x32 is a 50 ms sampling interval.

# The app is platform independent, so build once on the build machine for every target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY ["DeveAgentManager/DeveAgentManager.csproj", "DeveAgentManager/"]
COPY ["DeveAgentManager.Client/DeveAgentManager.Client.csproj", "DeveAgentManager.Client/"]
COPY ["DeveAgentManager.Contracts/DeveAgentManager.Contracts.csproj", "DeveAgentManager.Contracts/"]
COPY ["DeveAgentManager.Core/DeveAgentManager.Core.csproj", "DeveAgentManager.Core/"]
COPY ["DeveAgentManager.Providers.Proxmox/DeveAgentManager.Providers.Proxmox.csproj", "DeveAgentManager.Providers.Proxmox/"]
COPY ["DeveAgentManager.Harnesses.Pi/DeveAgentManager.Harnesses.Pi.csproj", "DeveAgentManager.Harnesses.Pi/"]
COPY ["DeveAgentManager.Harnesses.Claude/DeveAgentManager.Harnesses.Claude.csproj", "DeveAgentManager.Harnesses.Claude/"]
COPY ["DeveAgentManager.Harnesses.Codex/DeveAgentManager.Harnesses.Codex.csproj", "DeveAgentManager.Harnesses.Codex/"]
RUN dotnet restore "DeveAgentManager/DeveAgentManager.csproj"
COPY . .
ARG BUILD_VERSION
ARG VER=${BUILD_VERSION:-1.0.0}
RUN dotnet publish "DeveAgentManager/DeveAgentManager.csproj" -c Release -o /app/publish /p:Version=$VER /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "DeveAgentManager.dll"]
