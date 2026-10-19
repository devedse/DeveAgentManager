#!/usr/bin/env bash
set -euo pipefail

image=${1:?Usage: verify-container.sh IMAGE OUTPUT_DIRECTORY}
output=${2:?Usage: verify-container.sh IMAGE OUTPUT_DIRECTORY}
mkdir -p "$output"
output=$(realpath "$output")
mkdir -p "$output/result"
chmod 1777 "$output/result"
container=$(docker run --detach --cap-drop ALL --security-opt no-new-privileges --read-only --ulimit core=0 \
    --env CPU_PROFILER_ENABLED=true --tmpfs /tmp:rw,size=256m \
    --mount "type=bind,src=$output/result,dst=/result" "$image")
trap 'docker rm --force "$container" >/dev/null 2>&1 || true' EXIT

# A native deadlock can ignore managed cancellation. Bound the container from outside
# and retain its logs and partial capture before removing it.
status=0
exit_code=$(timeout --signal=TERM --kill-after=5s 120s docker wait "$container") || status=$?
docker logs "$container" > "$output/console.log" 2>&1
cat "$output/console.log"
docker cp "$container:/tmp/dam-cpu-profiler" "$output/native-logs" 2>/dev/null || true
if [[ "$status" != 0 || "$exit_code" != 0 ]]; then
    docker inspect --format '{{json .State}}' "$container" > "$output/container-state.json"
    docker top "$container" -eo pid,ppid,stat,comm,wchan > "$output/processes.txt" 2>&1 || true
    cat "$output/container-state.json"
    cat "$output/processes.txt"
    echo "CPU profiler verification failed (wait status=$status, container exit=$exit_code)." >&2
    exit 1
fi
