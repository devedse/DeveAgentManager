#!/bin/sh
set -eu
# ARM64's experimental native unwinder has not passed CPU attribution validation.
# Use portable traces there unless explicitly testing the native engine.
case "$(uname -m)" in aarch64) CPU_PROFILER_DEFAULT=false ;; *) CPU_PROFILER_DEFAULT=true ;; esac
if [ "${CPU_PROFILER_ENABLED:-$CPU_PROFILER_DEFAULT}" = true ]; then
    case "$(uname -m)" in aarch64) export DOTNET_TC_QuickJitForLoops=0 ;; esac
    CPU_PROFILER_TOKEN=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
    export CPU_PROFILER_TOKEN CPU_PROFILER_ENABLED=true
    export CORECLR_ENABLE_PROFILING=1
    export CORECLR_PROFILER='{BD1A650D-AC5D-4896-B64F-D6FA25D6B26A}'
    export CORECLR_PROFILER_PATH=/app/cpu-profiler/Pyroscope.Profiler.Native.so
    export LD_LIBRARY_PATH=/app/cpu-profiler
    for CPU_PROFILER_WRAPPER in /app/cpu-profiler/Pyroscope.Linux.ApiWrapper.*.so; do
        if [ -f "$CPU_PROFILER_WRAPPER" ]; then export LD_PRELOAD="$CPU_PROFILER_WRAPPER"; break; fi
    done
    export PYROSCOPE_PROFILING_ENABLED=1
    export PYROSCOPE_PROFILING_CPU_ENABLED=1
    # The pinned engine requires this additional opt-in on native ARM64.
    export DD_INTERNAL_PROFILING_ENABLED_ARM64=1
    # The contention setting is presence-sensitive in this engine: unset disabled types instead of "false".
    unset PYROSCOPE_PROFILING_WALLTIME_ENABLED PYROSCOPE_PROFILING_ALLOCATION_ENABLED PYROSCOPE_PROFILING_LOCK_ENABLED
    unset PYROSCOPE_PROFILING_EXCEPTION_ENABLED PYROSCOPE_PROFILING_HEAP_ENABLED PYROSCOPE_PROFILING_GC_ENABLED
    unset DD_PROFILING_WALLTIME_ENABLED DD_PROFILING_ALLOCATION_ENABLED DD_PROFILING_LOCK_ENABLED
    unset DD_PROFILING_EXCEPTION_ENABLED DD_PROFILING_HEAP_ENABLED DD_PROFILING_GC_ENABLED
    export DD_GC_THREADS_CPUTIME_ENABLED=0
    export PYROSCOPE_SERVER_ADDRESS="http://127.0.0.1:4042/$CPU_PROFILER_TOKEN"
    export PYROSCOPE_APPLICATION_NAME=deveagentmanager
    export DD_PROFILING_UPLOAD_PERIOD=2
    export DD_INTERNAL_CPU_PROFILING_INTERVAL=19
    export DD_PROFILING_FRAMES_NATIVE_ENABLED=true
    export DD_TRACE_DEBUG=false
    export DD_TRACE_LOG_DIRECTORY=/tmp/dam-cpu-profiler
else
    export CPU_PROFILER_ENABLED=false
fi
if [ "$#" -gt 0 ] && [ "${1#-}" = "$1" ]; then exec "$@"; fi
exec dotnet DeveAgentManager.dll "$@"
