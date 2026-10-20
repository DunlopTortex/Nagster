# DG Nagging Alarms

A Windows WPF tray application for persistent alarms and calendar events.

## Run

Install with `dist/DGNaggingAlarms-Setup-0.1.0.exe`, or run the self-contained executable in `publish/DGNaggingAlarms.exe`. The installer registers the tray app to start when you sign in. The tray icon opens the calendar, new-alarm form, active-alarm center, and settings. Closing the calendar keeps alarms running in the tray; use **Exit** from the tray menu to stop the app.

Command-line arguments:

- `-new`: open the new alarm/event form.
- `-quick`: open a compact reminder form with time presets.
- `-calendar`: open the main calendar and agenda.
- `-settings`: open settings.
- `-tray`: start in the tray without opening a window.

If the app is already running, a second invocation forwards its command to the running instance.

## Features

- One-time and recurring alarms: minutes, hours, daily, selected weekdays, monthly day, nth/last weekday, and yearly. Recurrence can stop on a date or after a count.
- Calendar events with an end time and reminder offset.
- Optional absolute or per-occurrence completion deadline. Manual and automatic snoozes cannot pass it.
- Configurable snooze buttons, 30-second default response timeout, and one-hour default automatic snooze. Response rules can be overridden for each alarm.
- Per-alarm WAV/MP3 sound with default fallback. Active sounds play in three-play bursts separated by one minute. Concurrent alarm sounds are serialized.
- Always-on-top semi-transparent popup stack with an active-alarm center for overflow.
- Calendar import from Google, Microsoft 365, desktop Outlook and secret ICS links, with ignore
  rules and no duplicates. See [calendar setup](docs/calendar-setup.md).
- SQLite storage at `%LOCALAPPDATA%\DGNaggingAlarms\alarms.db`.

**Dismiss** stops that occurrence's alert and leaves it incomplete in the agenda. **Complete** marks it done. **Reschedule** moves this occurrence's next alert and leaves the recurrence rule unchanged.

## Build

Requires the .NET 10 SDK and Inno Setup 6.

```powershell
dotnet build DGNaggingAlarms\DGNaggingAlarms.csproj -c Release
dotnet run --project Tests\DGNaggingAlarms.Checks\DGNaggingAlarms.Checks.csproj -c Release
./build-installer.ps1
```

The installer is unsigned. A trusted code-signing certificate is needed before wider distribution.

## Current limits

- The calendar provides month selection and a dated agenda, but no drag-and-drop editing.
- Popup placement currently supports four corners of the primary work area. Monitor selection, margins, width, and advanced themes remain to be built.
- Editing an existing recurring series, recurrence exclusions, quiet hours, import/export, and explicit daylight-saving-time policy remain to be built.
- Events use the same alarm popup and nagging rules as alarms.
