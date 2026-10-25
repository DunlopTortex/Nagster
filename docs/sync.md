# Syncing the iPhone with the PC

The iPhone app keeps its alarms in step with Nagster on your PC. The PC holds the master copy; the
phone syncs with it through the PC's web interface whenever it can reach it, and keeps working (and
nagging) when it can't. To stay in step from anywhere, with or without a PC, there's also
[cloud sync](cloud-sync.md), end-to-end encrypted, which follows the same rules.

## Setting it up

1. On the PC, open **Settings → Web access** in Nagster, turn it on and set a password.
2. On the iPhone, open **Settings** in Nagster, enter the PC's address and the password, and tap
   **Sign in**.
   - At home, use the PC's address on your network, e.g. `192.168.1.20` (port 5972 is assumed).
   - To sync from anywhere, install [Tailscale](https://tailscale.com) on both and use the PC's
     Tailscale IP address, e.g. `100.101.102.103`. Tailscale names (`my-pc.tailnet.ts.net`) don't
     work, because iOS only allows the plain HTTP the PC serves to local addresses.

The phone stays signed in for six months of not being used, like a browser. Signing out on the PC
(**Sign out all devices**) or changing the password signs it out.

On your home network the password travels unencrypted, as it does for the existing web interface.
Over Tailscale everything is encrypted.

## When it syncs

- When the app opens or comes back to the foreground.
- A couple of seconds after any change on the phone.
- Straight after you answer a notification or an Important alarm, so the PC's popup closes too.
- Now and then in the background, when iOS allows it (iOS decides how often).

## Which devices ring

Every alarm has a **Ring on** setting: **All devices** (the default), **PC only** or **iPhone
only**. Every device lists every alarm; only the chosen ones sound. Answering on one device answers
it everywhere at the next sync.

**Important** (iPhone only) makes an alarm ring full screen through AlarmKit, like the Clock app,
even in silent mode and Focus. Its **Stop** button completes it and **Snooze** snoozes for the first
of your snooze choices; other choices, Dismiss and Reschedule are in the app.

## How the phone nags

The PC nags by ringing again after each automatic snooze until someone answers. Notifications can't
wait for an answer, so the phone schedules the repeats in advance. Each device has its own interval,
10 minutes unless changed: the PC's automatic snooze length in its Settings, and **Nag again after**
in the phone's Settings. An alarm can also have its own **Nag again after** length, set in either
app's editor and synced: it overrides both settings for that alarm. On the phone, nagging can also
be turned off altogether in Settings, or for one alarm (its **Nag again after** set to **Off**);
that choice stays on the phone, and the PC keeps nagging. Reminders stop at the alarm's deadline, or
when its next occurrence comes due. iOS allows an app only 64 scheduled notifications, so the phone
keeps the soonest 60 and tops them up each time it opens or syncs.

Each notification has **Complete**, up to four snooze buttons (the first of the phone's snooze
choices) and **Dismiss**. Press and hold, or swipe down, to see them all.

Alarm hours are per device too. Each device snoozes its own single-device alarms (PC only, iPhone
only) that fall outside its hours until they start, and the snooze syncs like any other. Alarms for
all devices ignore both devices' hours, so one device's evening cut-off never silences the other.

Pausing alerts is per device: pausing the PC doesn't silence the phone, or the other way round.

Snooze choices start out shared: the phone uses the PC's list. Changing the list in the phone's
Settings gives the phone its own, which isn't synced; **Use the PC's list** goes back to the PC's.

## How conflicts are settled

The phone sends what you did (completed, dismissed, snoozed, rescheduled, added, edited or deleted
an alarm), not its copy of the data. The PC applies each action with these rules, and never compares
the two devices' clocks:

- **A finished alarm never reopens.** Completed beats Dismissed, which beats Missed, which beats
  anything still waiting. Completing on the phone after it was dismissed on the PC still completes
  it.
- **Otherwise the phone's action wins**, unless someone acted on the PC since the phone last saw
  that alarm. Things the PC did by itself (showing a popup, an automatic snooze) don't count.
- **Edits to an alarm** apply unless it was changed on the PC since the phone last synced, in which
  case the PC's version is kept. A deleted alarm isn't brought back by an older edit.

Actions the PC turns down are listed under **Your PC** in the phone's Settings, with the reason.

## The protocol

`POST /api/sync`, signed in with the session cookie and carrying the `X-DGNA: 1` header like every
other change to the web interface.

**Request:** `{ deviceId, sinceRevision, ops[] }`. Each op has an `opId` (the PC applies each only
once), a `type` (`complete`, `dismiss`, `snooze`, `reschedule`, `upsertAlarm`, `deleteAlarm`) and
the `baseRevision` of the record when the phone acted. Occurrence ops also carry the alarm ID and
scheduled time, so the PC can create an occurrence the phone worked out ahead of it.

**Reply:** `{ revision, full, definitions, occurrences, tombstones, settings, results }`. With
`full` the phone replaces its copy (the first sync, or after being away long enough to miss pruned
deletions); otherwise it applies the changes since `sinceRevision`.

Details that keep the two apps in agreement:

- **Times are wall-clock**, `yyyy-MM-ddTHH:mm:ss` with no time zone, as the PC stores them. A daily
  09:00 alarm stays at 09:00 through the clocks changing and when travelling.
- **Occurrence IDs are derived** from the alarm ID and scheduled time (a version-5-style UUID over
  SHA-1 of `"{alarm id}|{time}"`), so both apps name the same occurrence the same way.
- **The PC generates occurrences**: about 90 days ahead, or 3 days (up to 300) for alarms repeating
  every few minutes or hours. Beyond that, and for alarms added on the phone before they sync, the
  phone works them out from the same repeat rules.
- The PC's settings for snooze choices, automatic snooze and response time come with every reply.
  The phone uses the snooze choices unless it has its own list.

`shared/parity-cases.json` and the sample request and reply in `shared/` are checked by both apps'
tests, so a change to the repeat rules, IDs or JSON on one side fails the other's tests.
