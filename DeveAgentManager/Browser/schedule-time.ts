// The browser owns the device setting; all conversion and DST validation live in BrowserClock.cs.
export function timeZone(): string {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
}
