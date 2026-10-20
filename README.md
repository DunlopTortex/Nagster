# DG Nagging Alarms

Persistent alarms and calendar reminders that keep nagging until you deal with them. Unlike a
standard notification, an alarm stays on screen and repeats on a schedule until you complete,
snooze, dismiss or reschedule it.

## Platforms

| Folder                 | Platform | Status                                         |
| ---------------------- | -------- | ---------------------------------------------- |
| [`windows/`](windows/) | Windows  | Working first release (WPF tray app, .NET 10)  |
| `ios/`                 | iOS      | Planned                                        |

## Windows app at a glance

- One-time and recurring alarms, plus calendar events with reminder offsets
- Completion deadlines that snoozes cannot push past
- Always-on-top popup stack with an active-alarm centre for overflow
- Calendar import from Google, Microsoft 365, desktop Outlook and ICS links
- Optional Pushover notification to your phone the first time an unanswered alarm auto-snoozes
  each day
- Optional password-protected web interface for managing alarms from other devices on your network
- Local SQLite storage, with no cloud account required

See the [Windows README](windows/README.md) for installation, command-line options, build steps
and current limits, and [calendar setup](windows/docs/calendar-setup.md) for connecting calendars.
The longer-term roadmap is in the [product plan](windows/PRODUCT_PLAN.md).

## Quick build (Windows)

Requires the .NET 10 SDK, plus Inno Setup 6 for the installer.

```powershell
cd windows
dotnet build DGNaggingAlarms\DGNaggingAlarms.csproj -c Release
dotnet run --project Tests\DGNaggingAlarms.Checks\DGNaggingAlarms.Checks.csproj -c Release
./build-installer.ps1
```
