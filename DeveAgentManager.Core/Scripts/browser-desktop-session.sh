#!/bin/sh
# The browser desktop's session (KasmVNC's xstartup): XFCE. Logging out ends it, and the service starts a new one.
unset SESSION_MANAGER DBUS_SESSION_BUS_ADDRESS
export DAM_BROWSER_DESKTOP=1 XDG_SESSION_TYPE=x11 XDG_CURRENT_DESKTOP=XFCE
exec dbus-launch --exit-with-session startxfce4
