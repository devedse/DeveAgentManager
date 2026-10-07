#!/usr/bin/env bash
# Isolated published app only. Requires Docker; never point this script at a deployed Manager.
set -euo pipefail
scratch=$(mktemp -d)
trap 'docker rm -f dam-zap-test >/dev/null 2>&1 || true; rm -rf "$scratch"' EXIT
chmod 777 "$scratch"
docker build -t dam-zap-test-image .
docker run -d --name dam-zap-test --network host -e ASPNETCORE_URLS=http://127.0.0.1:19081 \
  -e ADMIN_PASSWORD=isolated-zap-password-1234 -e PROXMOX_URL= dam-zap-test-image
for i in $(seq 1 120); do if curl -fsS http://127.0.0.1:19081/health >/dev/null; then break; fi; sleep 1; done
# Passive anonymous baseline. Warnings are recorded (exit 2); explicit failures and scanner errors fail.
set +e
docker run --rm --network host -v "$scratch:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t http://127.0.0.1:19081/login -r anonymous.html -J anonymous.json -m 1
result=$?
set -e
if [[ "$result" != 0 && "$result" != 2 ]]; then exit "$result"; fi
# Obtain a test-only session and configure ZAP to attach it. No credentials appear in command arguments or output.
python3 - "$scratch" <<'PY'
import html, json, re, sys, urllib.request, urllib.parse, http.cookiejar
jar=http.cookiejar.CookieJar()
opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
body=opener.open('http://127.0.0.1:19081/login').read().decode()
token=html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"',body)[1])
req=urllib.request.Request('http://127.0.0.1:19081/account/login',data=urllib.parse.urlencode({'username':'admin','password':'isolated-zap-password-1234','__RequestVerificationToken':token}).encode(),headers={'Origin':'http://127.0.0.1:19081'})
opener.open(req).read()
cookie='; '.join(c.name+'='+c.value for c in jar)
config='replacer.full_list(0).description=isolated-test-session\nreplacer.full_list(0).enabled=true\nreplacer.full_list(0).matchtype=REQ_HEADER\nreplacer.full_list(0).matchstr=Cookie\nreplacer.full_list(0).regex=false\nreplacer.full_list(0).replacement='+cookie+'\n'
open(sys.argv[1]+'/session.properties','w').write(config)
PY
set +e
docker run --rm --network host -v "$scratch:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t http://127.0.0.1:19081/ -r authenticated.html -J authenticated.json -m 1 -z '-configfile /zap/wrk/session.properties'
result=$?
set -e
mkdir -p .validation/zap
cp "$scratch"/*.html "$scratch"/*.json .validation/zap/
if [[ "$result" != 0 && "$result" != 2 ]]; then exit "$result"; fi
