# Timed access

Access to a harness login, a Runner, a project (an agent and its workspaces) or a conversation can have a **time limit**.
When the time is up, the Manager ends the access by itself, the same way **Remove access** would.

## Setting it

- **When adding someone** (**Add user** in the access list): switch on **Time limit**. Either keep **For a duration** and
  enter minutes, hours or days, or switch it off and pick (or type) an **End date** and **End time**. Dates and times
  are in the browser's timezone, which the dialog shows, as in **Schedule continue**.
- **Later**: choose **Time limit…** from the person's ⋮ menu. It shows when the access ends now. You can set a new duration
  or end time, or switch **Time limit** off to remove it.
- The list shows the end in local time and how long is left: `Use · until 2026-11-19 04:30 (4 hours, 59 minutes left)`.

A duration is counted on the Manager's clock, so a wrong device clock doesn't shorten or lengthen it. An end time must
be in the future and at most 366 days away; so must a duration. A local time that doesn't exist or occurs twice
because the clocks change is refused; use a duration then.

Changing someone's level (Use ↔ Manage, read-only ↔ read-write) keeps their time limit. In the API, every request
that gives or changes access takes an optional `expiry`: left out, the access keeps its time limit. `{}` means no time
limit, `{ "minutes": 300 }` sets a duration and `{ "until": "2026-11-19T03:30:00Z" }` sets an end time in UTC. The
conversation share request's body is optional.

## When it ends

`AccessExpiryService` checks when the Manager starts and every 10 seconds after that, so access ends within seconds of
its time, including after a restart. Ending access works like removing it:

| Access | What ending it does |
| --- | --- |
| Harness login | Their agents using the login stop (offline stops are retried), and it is no longer their default. |
| Runner | Their agents on the Runner are removed (conversations stay theirs), and so are their files and keys on it. |
| Project | The share ends for every workspace, including ones created later (they copy the time limit). |
| Conversation | The share ends. |

Open terminals and live pages of that person are checked again straight away (`AccessChanged`). The audit log records
the change without an actor, with the detail `<username>: time limit ended`. If ending Runner access or a share fails,
for example because an agent can't be removed yet, the next check tries again.

## Logins always keep a lasting manager

A login has no owner, so it needs a manager whose access doesn't run out. Manage access can only have a time limit while
another enabled manager has none. The last manager without a time limit can't be removed, given a time limit or
disabled while the other managers have one.
