#!/usr/bin/env bash
# Isolated published app only. Requires Docker; never point this script at a deployed Manager.
set -euo pipefail
scratch=$(mktemp -d)
trap 'docker rm -f dam-zap-test >/dev/null 2>&1 || true; rm -rf "$scratch"' EXIT
chmod 777 "$scratch"
scan_image="${DAM_SCAN_IMAGE:-dam-zap-test-image}"
base="http://127.0.0.1:${DAM_SCAN_PORT:-19081}"
if [[ -z "${DAM_SCAN_IMAGE:-}" ]]; then docker build -t "$scan_image" .; fi
docker run -d --name dam-zap-test --network host -e ASPNETCORE_URLS="$base" \
  -e ADMIN_PASSWORD=isolated-zap-password-1234 -e PROXMOX_URL= "$scan_image"
ready=false
for i in $(seq 1 120); do
  if [[ "$(docker inspect -f '{{.State.Running}}' dam-zap-test)" != true ]]; then docker logs dam-zap-test; exit 1; fi
  if curl -fsS "$base/health" >/dev/null; then ready=true; break; fi
  sleep 1
done
if [[ "$ready" != true ]]; then docker logs dam-zap-test; exit 1; fi
# Passive anonymous baseline. Warnings are recorded (exit 2); explicit failures and scanner errors fail.
set +e
docker run --rm --network host -v "$scratch:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t "$base/login" -r anonymous.html -J anonymous.json -m 1
result=$?
set -e
if [[ "$result" != 0 && "$result" != 2 ]]; then exit "$result"; fi
# Obtain a test-only session and configure ZAP to attach it. No credentials appear in command arguments or output.
python3 - "$scratch" "$base" <<'PY'
import html, json, re, sys, urllib.request, urllib.parse, http.cookiejar
jar=http.cookiejar.CookieJar()
opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
base=sys.argv[2]
body=opener.open(base+'/login').read().decode()
token=html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',body)[1])
req=urllib.request.Request(base+'/account/login',data=urllib.parse.urlencode({'username':'admin','password':'isolated-zap-password-1234','__RequestVerificationToken':token}).encode(),headers={'Origin':base})
opener.open(req).read()
cookie='; '.join(c.name+'='+c.value for c in jar)
config='replacer.full_list(0).description=isolated-test-session\nreplacer.full_list(0).enabled=true\nreplacer.full_list(0).matchtype=REQ_HEADER\nreplacer.full_list(0).matchstr=Cookie\nreplacer.full_list(0).regex=false\nreplacer.full_list(0).replacement='+cookie+'\n'
open(sys.argv[1]+'/session.properties','w').write(config)
PY
set +e
docker run --rm --network host -v "$scratch:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t "$base/" -r authenticated.html -J authenticated.json -m 1 -z '-configfile /zap/wrk/session.properties'
result=$?
set -e
mkdir -p .validation/zap
cp "$scratch"/*.html "$scratch"/*.json .validation/zap/
if [[ "$result" != 0 && "$result" != 2 ]]; then exit "$result"; fi
