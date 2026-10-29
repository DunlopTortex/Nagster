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
  repeats until answered, every 10 minutes unless you change **Nag again after** in Settings, or for
  one alarm in its editor. Nagging can be turned off for the whole app (**Nag until answered**) or
  for one alarm, on the iPhone only.
- **Important** alarms ring full screen through AlarmKit, like the Clock app, even in silent mode.
- Pauses alerts on the phone for a while, independently of the PC.
- **Snooze choices** can be changed in Settings, with or without a PC. With one, the phone uses the
  PC's list until you change it here.
- **Alarm hours**: outside them the phone stays silent unless the alarm is Important; anything that
  comes due waits and alerts when they start. The PC has its own hours.

Calendar import and Pushover stay on the PC; imported alarms reach the phone through sync. See
[syncing](../docs/sync.md) for setup, how nagging works on the phone, and how conflicts are settled, and
[cloud sync](../docs/cloud-sync.md) for syncing from anywhere, end-to-end encrypted, with or without a PC.

## Requirements

- iOS 26.1 or later (for AlarmKit).
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

GitHub Actions builds the app and runs both test suites when you start the **iOS** workflow from the
Actions tab (see `.github/workflows/ios.yml`). It builds for the Simulator without signing.

On a Mac with Xcode 26:

```sh
brew install xcodegen
cd ios
(cd NagsterCore && swift test)
xcodegen generate
open Nagster.xcodeproj
```

## Installing on an iPhone

### Sideloading the CI build (free Apple ID)

Every CI run attaches an unsigned build as the **Nagster-ipa** artifact. Unzip it and install
`Nagster-unsigned.ipa` with a tool such as Sideloadly, which signs it with your Apple ID. Turn on
Developer Mode on the phone first. With a free Apple ID the app stops opening after 7 days until it
is installed again.

### From Xcode on a Mac

Generate the project as above, then for both the Nagster and NagsterWidgets targets choose your team
under **Signing & Capabilities** and give them bundle IDs your account can own (the widget's must
start with the app's). A free personal team can't use Time Sensitive Notifications; remove that
capability if Xcode objects. Choose your iPhone as the destination and press ⌘R. Running `xcodegen
generate` again resets these signing choices.

### TestFlight (paid Apple Developer account)

`.github/workflows/testflight.yml` builds, signs and uploads to TestFlight when it's run from the
Actions tab (run it on a tag such as `ios-v0.2.0` and the tag sets the version). Xcode signs
with the App Store Connect API key, so no certificates are stored in the repository. Until the
secrets below exist, the workflow skips the upload.

One-off setup:

1. In the Apple Developer portal, register the app ID `com.dgutilities.nagster` with **Time
   Sensitive Notifications**, and `com.dgutilities.nagster.widgets`. If either ID is taken, change
   the bundle IDs in `project.yml`.
2. In App Store Connect, create an app with that bundle ID. It never needs releasing.
3. Under **Users and Access → Integrations → App Store Connect API**, create a key with the
   **Admin** role. Admin is needed for Xcode to create the cloud-managed distribution certificate it
   signs uploads with; App Manager can archive but not upload. Note its Key ID and the Issuer ID,
   and download the `.p8` file (only possible once).
4. Add these repository secrets in GitHub (**Settings → Secrets and variables → Actions**):

   | Secret                        | Value                                                     |
   | ----------------------------- | --------------------------------------------------------- |
   | `APP_STORE_CONNECT_KEY_ID`    | The API key's Key ID                                      |
   | `APP_STORE_CONNECT_ISSUER_ID` | The Issuer ID shown above the keys                        |
   | `APP_STORE_CONNECT_KEY`       | The whole contents of the `.p8` file                      |
   | `APPLE_TEAM_ID`               | Your Team ID, from the developer portal's Membership page |

5. Install TestFlight on the iPhone and add yourself as an internal tester in App Store Connect.

To publish a build after that, see [publishing to TestFlight](../docs/Testflight_Publish.md). Each
upload takes 10–30 minutes to process before it appears in TestFlight. Build numbers come from the
workflow's run number, so they always increase. TestFlight builds stay installable for 90 days.
