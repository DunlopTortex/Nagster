# Nagster

A Windows WPF tray application for persistent alarms and calendar events.

## Run

Install with `dist/Nagster-Setup-0.3.1.exe`, or run the self-contained executable in
`publish/Nagster.exe`. The installer registers the tray app to start when you sign in. The tray icon
opens the calendar, new-alarm form, active-alarm center, and settings. Closing the calendar keeps
alarms running in the tray; use **Exit** from the tray menu to stop the app.

Command-line arguments:

- `-new`: open the new alarm/event form.
- `-quick`: open a compact reminder form with time presets.
- `-calendar`: open the main calendar and agenda.
- `-settings`: open settings.
- `-tray`: start in the tray without opening a window.

If the app is already running, a second invocation forwards its command to the running instance.

## Features

- One-time and recurring alarms: minutes, hours, daily, selected weekdays, monthly day, nth/last
  weekday, and yearly. Recurrence can stop on a date or after a count.
- Calendar events with an end time and reminder offset.
- Optional absolute or per-occurrence completion deadline. Manual and automatic snoozes cannot pass
  it.
- Configurable snooze buttons, 30-second default response timeout, and 10-minute default automatic
  snooze. Response rules can be overridden for each alarm.
- Per-alarm WAV/MP3 sound with default fallback. Active sounds play in three-play bursts separated
  by one minute. Concurrent alarm sounds are serialized.
- Always-on-top semi-transparent popup stack with an active-alarm center for overflow.
- Calendar import from Google, Microsoft 365, desktop Outlook and secret ICS links, with ignore
  rules and no duplicates. See [calendar setup](docs/calendar-setup.md).
- Optional password-protected web interface (Settings → Web access) for answering and editing alarms
  from a phone or another computer on your network.
- Sync with the [iPhone app](../ios/README.md) through the web interface. Each alarm can ring on all
  devices, the PC only or the iPhone only. See [syncing](../docs/sync.md).
- SQLite storage at `%LOCALAPPDATA%\Nagster\alarms.db`. Data from DG Nagging Alarms, the app's
  former name, is moved there automatically on first launch.

**Dismiss** stops that occurrence's alert and leaves it incomplete in the agenda. **Complete** marks
it done. **Reschedule** moves this occurrence's next alert and leaves the recurrence rule unchanged.

## Build

Requires the .NET 10 SDK and Inno Setup 6.

```powershell
dotnet build Nagster\Nagster.csproj -c Release
dotnet run --project Tests\Nagster.Checks\Nagster.Checks.csproj -c Release
./build-installer.ps1
```

The installer is unsigned. A trusted code-signing certificate is needed before wider distribution.

## Current limits

- The calendar provides month selection and a dated agenda, but no drag-and-drop editing.
- Popup placement currently supports four corners of the primary work area. Monitor selection,
  margins, width, and advanced themes remain to be built.
- Recurrence exclusions and import/export remain to be built.
- Alarm times are wall-clock times with no time zone, so a 09:00 alarm stays at 09:00 through the
  clocks changing.
- Events use the same alarm popup and nagging rules as alarms.
