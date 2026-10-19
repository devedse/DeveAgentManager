FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# 8080: the Portal. 8081: VS Code, which needs an origin of its own (see EDITOR_PORT in README.md).
EXPOSE 8080 8081
# /data holds the database, the encryption keys and the Manager's SSH key. Owned by the non-root "app" user,
# so a named volume mounted there starts with the right permissions.
RUN mkdir -p /data && chown app:app /data
ENV DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080;8081 \
    CPU_TRACE_STARTUP=false \
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
COPY ["DeveAgentManager.Harnesses.Grok/DeveAgentManager.Harnesses.Grok.csproj", "DeveAgentManager.Harnesses.Grok/"]
RUN dotnet restore "DeveAgentManager/DeveAgentManager.csproj"
COPY . .
ARG BUILD_VERSION
ARG VER=${BUILD_VERSION:-1.0.0}
RUN dotnet publish "DeveAgentManager/DeveAgentManager.csproj" -c Release -o /app/publish /p:Version=$VER /p:UseAppHost=false

# Only build-time tools; match native symbols to the target runtime, including arm64 builds.
FROM base AS cpu-diagnostics
ARG TARGETARCH
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3 binutils && rm -rf /var/lib/apt/lists/*
COPY tools/cpu-profiler/download-profiler.py tools/cpu-profiler/download-symbols.py /cpu-tools/
RUN python3 /cpu-tools/download-profiler.py
RUN python3 /cpu-tools/download-symbols.py
COPY --from=build /app/publish /publish
COPY tools/cpu-profiler/package-symbols.py /cpu-tools/
RUN python3 /cpu-tools/package-symbols.py

FROM base AS cpu-runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=cpu-diagnostics /cpu-profiler /app/cpu-profiler
COPY --from=cpu-diagnostics /diagnostics-symbols /app/diagnostics-symbols
COPY --from=cpu-diagnostics /runtime-symbols/ /usr/share/dotnet/shared/Microsoft.NETCore.App/
COPY tools/cpu-profiler/LICENSE /app/cpu-profiler/LICENSE
COPY tools/cpu-profiler/NOTICE /app/cpu-profiler/NOTICE
COPY tools/cpu-profiler/entrypoint.sh /app/cpu-profiler/entrypoint.sh
USER app
ENTRYPOINT ["sh", "/app/cpu-profiler/entrypoint.sh"]

FROM build AS cpu-test-build
RUN dotnet publish tools/cpu-profiler/probe/Probe.csproj -c Release -o /probe /p:Version=$VER /p:UseAppHost=false

# Executed by CI without root, extra capabilities, writable root filesystem or host perf access.
FROM cpu-runtime AS cpu-smoke
COPY --from=cpu-test-build /probe /probe
CMD ["dotnet", "/probe/Probe.dll"]

FROM cpu-runtime AS final
