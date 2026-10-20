# DGNaggingAlarms — product and implementation plan

The first working release is implemented in this workspace. See [README.md](README.md) for build/run instructions and the current feature limits. The sections below remain the longer-term product roadmap.

## Product goal

A local-first Windows tray app that makes important commitments hard to forget while keeping routine reminders quick to create. The tray app starts when the user signs in, keeps a small local database, and shows actionable WPF alarm windows. A separate main window provides an agenda, calendar, search, history, and settings.

## Core experience

### Fast creation

- Tray menu: **New alarm**, **New event**, **Quick reminder**, **Open calendar**, **Pause alerts**, **Settings**, **Exit**.
- Quick reminder accepts a title and a preset such as “in 10 minutes,” “in 1 hour,” or “tomorrow at 9.” A full editor is one click away.
- Full editor: required title, optional description, date/time, recurrence, must-complete-by deadline, priority, category, optional alarm sound, nagging policy, and optional link or file.
- Use explicit date/time fields and a live “Next 5 alerts” preview before saving. Natural-language entry can be added later, but should never silently interpret an ambiguous date.
- Command-line entry points: `DGNaggingAlarms.exe -new` opens the new-alarm editor, `-quick` opens quick reminder, and `-calendar` opens the main window. A second invocation forwards its request to the already-running tray instance and exits. If the app is not running, it starts the tray instance and opens the requested window.

### Main window

- **Today/Upcoming** agenda, **Calendar** (month/week/day), **Overdue**, **Completed**, and **All alarms** views.
- Search and filters by category, priority, status, and date.
- Dragging an item in the calendar opens a reschedule confirmation rather than silently changing a recurrence rule.

### Alarm popup

- A compact WPF window with the title, optional description preview, original due time, elapsed time, deadline countdown when set, and actions: **Complete**, **Dismiss**, **Snooze**, **Reschedule**, **Open details**. Dismiss stops the current alert without marking the occurrence complete; the occurrence remains visible in the agenda.
- Snooze offers configurable chips (for example 5, 15, 30 minutes and 1 hour), plus a custom date/time. The popup explains when a choice is unavailable because of the deadline.
- Reschedule opens a date/time picker and states whether the change affects this occurrence or the entire series. For recurring alarms, default to **this occurrence**.
- Position is configurable by monitor, screen corner, margin, width, stacking direction, and whether it follows the active monitor. Respect Windows work areas, DPI scaling, and multiple monitors.
- Configurable opacity, theme, accent, and animation. Alarm windows are always on top. Keep text and buttons opaque enough to read; provide a high-contrast option.
- Show up to a configurable number of alarm cards in a nonoverlapping stack, ordered by urgency (earliest deadline, then oldest due time). An overflow card shows the remaining count and opens an alarm center listing every active alarm. A new alarm joins the stack or overflow immediately without replacing the current one. Each visible card has its own countdown and actions; completing or snoozing one card does not alter the others. An alarm waiting in overflow must receive a full response period when it becomes visible.
- Play only one alarm sound at a time. Queue new alarm sound bursts so simultaneous alarms are audible and their chosen sounds do not overlap. Prioritize the same order as the visible stack. Sound playback must never block popup actions or timers.

## Scheduling and state rules

### Alarm versus event

- An **alarm** is an actionable occurrence that remains open until completed, dismissed, or explicitly rescheduled.
- An **event** has a start and optional end time, can carry one or more reminder offsets, and may be marked complete if the user wants task-like behavior. Events and alarms share the calendar, but their due and completion semantics remain distinct.

### Recurrence

The editor should offer:

- Once; every N minutes/hours/days/weeks/months/years.
- Selected weekdays; selected month days; first/second/third/fourth/last weekday of a month; last day of a month.
- Start date, optional end date or occurrence count, and optional exclusions/skipped dates.
- Optional repeat within a daily time window (for example every 30 minutes from 9:00 to 17:00).
- Explicit handling for short months (skip or use last day) and daylight-saving gaps/overlaps, with a preview of the resulting dates.

Store each recurrence rule separately from its generated occurrences. Materialize only a rolling horizon and extend it as time advances. Each occurrence gets its own completion, snooze, and reschedule history, so changing one occurrence does not corrupt the series.

### Must-complete-by deadline

- For a one-time alarm, the deadline is an absolute date/time.
- For a recurring alarm, support a deadline relative to each occurrence (for example 2 hours after its due time) or a chosen time on its occurrence day. A series end date is a separate concept.
- A snooze or reschedule that would move the next alert later than the deadline is rejected with a clear message and the latest permitted time. The user can still complete the alarm.
- Once the deadline passes, mark the occurrence **overdue** and keep it actionable. Escalation is configurable; the app should never auto-complete it.
- If an occurrence remains open when the next one begins, show both distinctly and allow “Complete this” or “Complete all overdue” with confirmation.

### Nagging policy

- Configurable first alert, repeat interval, escalation steps, quiet hours, and maximum concurrent popups.
- When an alarm card becomes visible and its first sound begins (or immediately if muted), start an independent response timer. If the user takes no action for 30 seconds by default, automatically snooze that occurrence for 1 hour. Both the timeout and automatic snooze duration are configurable in settings, with optional per-alarm overrides. The timer stops on Complete, Dismiss, Snooze, or Reschedule. Log automatic snoozes distinctly from manual ones. If Windows is locked or the app is recovering missed alarms, begin the timer when the alert can actually be presented.
- Use the alarm's selected sound when set; otherwise use the default sound. While an alarm is actively displayed, play its sound three times, wait one minute after the burst, then repeat three plays. Stop and cancel its queued sound immediately when the user acts or an automatic snooze succeeds. With the default 30-second response timeout, the second sound burst normally will not occur; it matters when the timeout is lengthened or disabled.
- Apply the deadline rule to automatic snooze too. If the proposed one-hour snooze would pass the must-complete-by time, leave the occurrence active, label it **Deadline prevents auto-snooze**, and keep its sound/nagging cycle running. Do not silently move it beyond the deadline.
- Optional escalation can change sound, accent, and repeat frequency as a deadline approaches or after it passes.
- Snooze delays only the selected occurrence. Reschedule changes its intended due time and records the original time in history.
- On app start, resume, unlock, and system time/time-zone changes, reconcile missed occurrences from stored data. Let the user choose whether to show all missed alarms, group them, or show only the latest within a configurable lookback period.
- Never depend on a long-running in-memory timer alone. Persist each state change before updating the UI.

## Settings organization

Use profile defaults, with optional per-alarm overrides:

1. **Appearance:** theme, accent, opacity, popup geometry, monitor, animation, fonts.
2. **Snooze and nagging:** preset buttons, custom snooze, 30-second default response timeout, one-hour default automatic snooze, repeat intervals, escalation, quiet hours, and maximum visible cards.
3. **Sound:** default sound, per-alarm sound override, volume, three-play burst with one-minute gap, silent mode, and a test button. Store sound references so a missing custom file falls back to the default with a visible warning in the editor.
4. **Scheduling:** time zone behavior, DST policy, missed-alert handling, week start.
5. **Startup and data:** start at sign-in, launch minimized, backup/export/import, retention.

Include a **Preview popup** button in settings and a **Restore defaults** action per section. Validate settings live so a popup cannot be positioned entirely off screen.

## Suggested architecture

- **Desktop:** WPF on .NET 10 LTS, MVVM, with a single-instance tray process and a Windows Forms `NotifyIcon` for the notification area.
- **Storage:** SQLite database under the user's local application data directory; versioned migrations, atomic updates, and a simple JSON export/import format.
- **Scheduler:** dedicated application service that calculates upcoming occurrences, persists transitions, sets the next wake timer, and reconciles after resume/startup/time changes. It owns recurrence, independent per-occurrence response timers, automatic snooze, deadline validation, and missed-alarm policy; UI commands call this service.
- **Audio coordinator:** owns per-alarm sound selection, three-play bursts, the one-minute repeat gap, and a single playback queue so sounds from concurrent alarms never overlap.
- **UI:** main calendar window, quick-add window, alarm popup host, settings window. Keep popup presentation separate from scheduling so its design can change without affecting timing logic.
- **Command line:** parse arguments at startup and send activation requests to the single running process through a local named pipe. Unknown arguments show concise usage feedback; activation brings the requested window to the foreground.
- **Windows integration:** start at user sign-in through the per-user Windows Run registry entry. The user can enable/disable it in settings, and Windows can also disable it in Startup Apps. Native Windows app notifications can be an optional backup channel; custom WPF windows remain the main alarm experience.
- **Installer:** Inno Setup package for per-user installation, Start menu shortcuts, and clean uninstall. A trusted production signing certificate is needed before wider distribution.
- **Icon:** create one recognizable alarm/bell mark that works at 16 px in the tray, in light and dark taskbars, and at larger sizes for the app and installer. Provide monochrome and color variants plus an overdue state.

## Build sequence

1. **Foundation:** WPF solution, tray lifecycle, single instance, database schema/migrations, installer skeleton, basic icon.
2. **Reliable alarms:** quick/full creation, `-new` activation, one-time alarms, always-on-top popup, complete/dismiss/snooze/reschedule, automatic snooze, sound loop, concurrent alarm stack, startup recovery, history.
3. **Recurrence:** daily/weekly/monthly/yearly rules, occurrence preview, exceptions, per-occurrence edits, DST tests.
4. **Calendar and events:** agenda/calendar views, event duration and reminder offsets, search and filters.
5. **Configuration and polish:** popup placement/transparency, sounds, nagging policies, accessibility, backup/import/export, signed installer and release QA.

## Initial release acceptance checks

- Install, sign in again, and verify the tray app starts once and alarms still fire.
- Create a one-time alarm in under 15 seconds; create a recurring alarm with the full editor and inspect its next five dates.
- Snooze and reschedule an occurrence; verify deadline enforcement and durable history.
- Leave two alarms unanswered at once; verify both remain accessible, their timers act independently, their sounds do not overlap, and neither action changes the other.
- Verify the 30-second automatic snooze, one-hour return, three-play sound burst, one-minute sound gap when the timeout is disabled, per-alarm sound fallback, and deadline-blocked automatic snooze.
- Run `DGNaggingAlarms.exe -new` both before and after the tray app starts; verify exactly one tray instance and one new-alarm window appear.
- Sleep through an alarm, restart the app, and change the system time zone; verify missed alarms reconcile without duplicates.
- Check popup placement and readability on two monitors with different scaling and with high-contrast settings.
- Uninstall and verify startup registration and program files are removed; user data behavior is documented and selectable.

## Decisions to confirm before implementation

1. Windows 11 only, or Windows 10 and 11?
2. Should “must complete by” prohibit **rescheduling** beyond the deadline as well as snoozing? This plan says yes.
3. Always-on-top is required; should alarms also interrupt full-screen apps, or wait until the user leaves full-screen mode?
4. Should syncing with Outlook/Google calendars be in scope, or should the first release remain local-only with import/export?
5. What should happen to personal data on uninstall: preserve it for reinstall, or offer deletion?
6. Does **Dismiss** mean “stop this alert but leave the item incomplete” (as planned), or “mark this occurrence complete”?

## Technical references

- [.NET 10 support policy](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
- [Windows Task Scheduler trigger types](https://learn.microsoft.com/en-us/windows/win32/taskschd/trigger-types)
- [Windows Forms NotifyIcon](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-overview-windows-forms)
- [MSIX desktop startup tasks](https://learn.microsoft.com/en-us/windows/uwp/porting/desktop-to-uwp-extensions)
- [Windows app notifications in WPF](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet)
