#!/bin/sh
# Runs apt-get update, then apt-get with the given arguments (e.g. "install -y python3" or "upgrade -y"), and removes
# the package lists. Mirrors can change indexes between download and verification, so the complete verified
# transaction is retried; package hash verification is never disabled and a partial index is never used.
set -eu
export DEBIAN_FRONTEND=noninteractive
for attempt in 1 2 3; do
  if apt-get update -o APT::Update::Error-Mode=any -o Acquire::Retries=3 \
      && apt-get -o Acquire::Retries=3 "$@"; then
    rm -rf /var/lib/apt/lists/*
    exit 0
  fi
  rm -rf /var/lib/apt/lists/*
  if [ "$attempt" = 3 ]; then exit 1; fi
  sleep "$((attempt * 5))"
done
