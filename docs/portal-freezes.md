# Portal freeze reports

Sometimes the Portal stops responding in a browser: an endless loop or long work blocks the page's main thread, so
nothing on it moves (not even the Damagochi), clicks do nothing and even Reload waits. When this can't be reproduced on
demand, the Manager's log still keeps a trace of it.

## How it works

`DeveAgentManager/Browser/freeze-watch.ts` starts with the Portal, before Blazor. Every second it tells a worker
(`freeze-watch-worker.ts`) that the page still answers, with its path, memory in use and (every five seconds) the
number of elements on the page. It also notes what happens:

- clicks: a button's `aria-label` or text (up to 40 characters), or a link's path, never other text on the page;
- navigations, including Back and Forward (the Navigation API, or `popstate` where it is missing);
- slow frames of 200 ms or more, with the scripts that took the time (Long Animation Frames, in Chromium; long tasks
  elsewhere where supported);
- script errors;
- sections loading, finishing (with how long it took to load and draw) and being left (`LiveComponent`, through
  `FreezeWatch.Note`).

The worker keeps the last 28 notes and, separately, the last 20 section notes, so a busy page doesn't push clicks
and navigations out.

The worker keeps running while the page doesn't. Once the page hasn't answered for **10 seconds** while it is shown,
the worker sends the notes to `POST /api/diagnostics/portal-freeze`. If the page answers again, it sends a second
report three seconds later, with how long the freeze lasted and what came after. The slow frame that was the freeze
names the script that took the time.

Some pauses are not reported:

- a hidden tab, whose timers the browser slows down;
- a suspended browser or computer, which the worker notices because its own timers ran late too.

A page paused in the browser's debugger looks frozen, so it is reported.

## In the Manager's log

The Manager writes each report as a **warning** under `DeveAgentManager.Diagnostics.PortalFreezeLog`. To find them,
search **Manage → Manager logs** for `stopped responding`. Each person sends at most ten reports an hour. A report looks
like this:

```text
The Portal stopped responding in alice's browser: no answer for 10 s, still frozen (page /sessions, open for 3 h 2 min).
Memory: .NET 1,234 MB, JavaScript 210 MB, 16,234 elements on the page.
Browser: Mozilla/5.0 (…) Chrome/…
What happened before, in seconds before its last answer:
-7.3 s  click link → /sessions/00000000-0000-0000-0000-000000000000
-7.2 s  load SessionDetail
-7.1 s  back or forward /sessions
-7.1 s  left SessionDetail
-7.0 s  loaded SessionList in 21 ms
```

How to read one:

- **The last notes** say what was going on when the page stopped answering. A `load` without its `loaded` means that
  section was still loading or drawing.
- **The .NET figure** is the WebAssembly memory. It only grows, so a high figure on a page that has been open for
  hours points at memory that kept growing.
- **A recovery report's slow frame** names the script that took the time.

Reports contain page paths and button labels, not the page's text. Each line of a report is one line in the log:
control characters the browser sent are replaced, and texts are shortened.

## Tests

- `DeveAgentManager.Tests/Browser/portal-freeze.mts` checks:
  - that a page blocked for 16 seconds is reported while frozen, with the click, Back, the section that was left,
    slow frames and no page text;
  - that it is reported again once it answers;
  - that a 4-second stall and a hidden tab are not reported.
- `PortalFreezeReportTests` checks:
  - that the report is written to the log;
  - that only signed-in Portal pages can send one;
  - the one-line-per-note rule and the length limits;
  - the limit of ten an hour per person.
