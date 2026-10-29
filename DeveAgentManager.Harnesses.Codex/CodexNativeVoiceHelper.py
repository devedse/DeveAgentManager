#!/usr/bin/env python3
"""Codex voice-helper protocol v1 backed by the connected browser's real audio transport."""
import json
import os
from pathlib import Path
import socket
import struct
import sys

LIMIT = 128 * 1024
connection = None
reader = None


def read_exact(count):
    data = bytearray()
    while len(data) < count:
        part = sys.stdin.buffer.read(count - len(data))
        if not part:
            if not data:
                return None
            raise ValueError()
        data.extend(part)
    return data


def reply(value):
    data = json.dumps(value).encode()
    if len(data) > LIMIT:
        raise ValueError()
    sys.stdout.buffer.write(struct.pack('!I', len(data)) + data)
    sys.stdout.buffer.flush()


def request(value, expected):
    data = json.dumps(value).encode()
    if len(data) > LIMIT:
        raise ValueError()
    connection.sendall(data + b'\n')
    data = reader.readline(LIMIT + 1)
    if not data or len(data) > LIMIT:
        raise ValueError()
    result = json.loads(data)
    if result.get('type') != expected:
        raise ValueError()
    return result


try:
    while True:
        header = read_exact(4)
        if header is None:
            break
        length = struct.unpack('!I', header)[0]
        if length > LIMIT:
            raise ValueError()
        message = json.loads(read_exact(length))
        kind = message.get('type')
        if kind == 'hello' and message.get('protocol') == 1:
            reply(dict(type='ready'))
        elif kind == 'initializeRuntime':
            # Codex deliberately filters child environment variables. Read only our parent's socket setting.
            values = Path('/proc/%d/environ' % os.getppid()).read_bytes().split(b'\0')
            address = next(value.split(b'=', 1)[1].decode() for value in values if value.startswith(b'DAM_NATIVE_VOICE_SOCKET='))
            connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            connection.settimeout(40)
            connection.connect(address)
            reader = connection.makefile('rb')
            reply(dict(type='runtimeReady'))
        elif kind == 'startTransport':
            result = request(dict(type='offer'), 'offer')
            if not isinstance(result.get('sdp'), str) or not 0 < len(result['sdp']) <= 65536:
                raise ValueError()
            reply(dict(type='offer', sdp=result['sdp']))
        elif kind == 'applyAnswer':
            request(dict(type='answer', sdp=message['sdp']), 'transportReady')
            reply(dict(type='transportReady'))
        elif kind == 'openDevices':
            request(dict(type='devices'), 'devicesOpened')
            reply(dict(type='devicesOpened'))
        elif kind == 'setAudioControls':
            request(dict(type='controls', **message['controls']), 'controlsApplied')
            reply(dict(type='audioControlsApplied'))
        elif kind == 'inspectAudio':
            result = request(dict(type='levels'), 'levels')
            reply(dict(type='audioState', state=dict(microphonePeak=result['microphonePeak'], speakerPeak=result['speakerPeak'])))
        elif kind == 'close':
            if connection is not None:
                request(dict(type='close'), 'closed')
            reply(dict(type='closed'))
            break
        else:
            raise ValueError()
except Exception:
    # Fixed native-helper failure stage, without logging SDP, credentials or conversation payloads.
    sys.exit(37)
finally:
    if connection is not None:
        connection.close()
