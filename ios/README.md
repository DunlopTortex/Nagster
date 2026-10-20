# Nagster for iPhone

Nagster's alarms on your iPhone, synced with [Nagster on your PC](../windows/README.md). An alarm
keeps nagging until you complete, snooze, dismiss or reschedule it, and answering it on either
device answers it on both.

## What it does

- Lists alarms going off now, coming up in the next fortnight, and answered this week.
- Adds and edits alarms and events with the same choices as the PC: repeats from every few minutes
  to yearly (weekdays, nth or last weekday, end date or count), event reminders, deadlines.
- **Ring on** each alarm: all devices, the PC only or the iPhone only.
- Rings with notifications by default. Each has **Complete**, snooze buttons and **Dismiss**, and
  repeats at the PC's automatic snooze interval until answered.
- **Important** alarms ring full screen through AlarmKit, like the Clock app, even in silent mode.
- Pauses alerts on the phone for a while, independently of the PC.

Calendar import, Pushover and the web interface stay on the PC; imported alarms reach the phone
through sync. See [syncing](../docs/sync.md) for setup, how nagging works on the phone, and how
conflicts are settled.

## Requirements

- iOS 26 or later (AlarmKit needs it).
- Nagster on the PC with **Settings → Web access** turned on, reachable over your home network or
  Tailscale.

## Project layout

| Path              | Contents                                                                                    |
| ----------------- | ------------------------------------------------------------------------------------------- |
| `project.yml`     | [XcodeGen](https://github.com/yonaskolb/XcodeGen) spec; the Xcode project is generated      |
| `NagsterCore/`    | Swift package with the logic shared with the PC: repeat rules, occurrence IDs, sync, alerts |
| `Nagster/`        | The app: SwiftUI screens, SwiftData storage, notifications, AlarmKit, sync client           |
| `NagsterWidgets/` | Widget extension with the Live Activity AlarmKit shows while an Important alarm is snoozed  |
| `Shared/`         | Code compiled into both the app and the widget                                              |
| `NagsterTests/`   | App tests run in the Simulator                                                              |

## Building

GitHub Actions builds the app and runs both test suites on every push that touches `ios/` or
`shared/` (see `.github/workflows/ios.yml`). It builds for the Simulator without signing.

On a Mac with Xcode 26:

```sh
brew install xcodegen
cd ios
(cd NagsterCore && swift test)
xcodegen generate
open Nagster.xcodeproj
```

## Installing on an iPhone

This needs a paid Apple Developer account. In Xcode, choose your team under **Signing &
Capabilities** for the Nagster and NagsterWidgets targets, change the bundle IDs
(`com.dgutilities.nagster`) to ones your account owns, and run on your phone. Time-sensitive
notifications need that capability enabled for the App ID. CI can be extended to sign and upload to
TestFlight once the certificate, profile and App Store Connect key are added as repository secrets.
