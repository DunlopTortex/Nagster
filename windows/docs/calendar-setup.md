# Calendar setup

Nagster can turn events from your calendars into alarms. Open **Settings → Calendars →
Manage calendars…** and add calendars in any of four ways. Each event is imported once: later
changes to the event don't change its alarm, and deleting the alarm doesn't bring it back.

| Connection | Setup | Freshness | Knows if you declined |
| --- | --- | --- | --- |
| Calendar link (ICS) | Paste a secret link | Google: minutes; Outlook: can lag hours | No |
| Google account | Register an app once (below) | Near real time | Yes |
| Microsoft 365 account | Register an app once (below) | Near real time | Yes |
| Outlook on this PC | None (classic Outlook only) | Whatever Outlook has synced | Yes |

## Calendar links (ICS)

A secret link gives read-only access to one calendar. Anyone who has it can read that calendar,
so the app stores it encrypted. If a link leaks, reset it in your calendar's settings.

- **Google Calendar:** Settings → select the calendar → *Integrate calendar* → copy **Secret
  address in iCal format**.
- **Outlook / Microsoft 365:** Settings → Calendar → *Shared calendars* → *Publish a calendar* →
  choose *Can view all details* → copy the **ICS** link. Some organisations turn publishing off,
  and published calendars can take several hours to reflect changes.

## Google account

Google only lets registered apps sign in. Registration is free and takes about ten minutes.

1. Open the [Google Cloud console](https://console.cloud.google.com/) and create a project, e.g.
   "Nagster".
2. In *APIs & Services → Library*, enable the **Google Calendar API**.
3. In *APIs & Services → OAuth consent screen*, choose **External**, give the app a name and your
   e-mail address, and add the scope `.../auth/calendar.readonly`. Add your Google account under
   **Test users**.
4. In *APIs & Services → Credentials*, choose **Create credentials → OAuth client ID**, type
   **Desktop app**. Copy the **client ID** and **client secret** into the app's
   *App registrations* section.
5. Choose **+ Google account** and sign in in the browser window that opens.

**About the 7-day sign-in limit:** while the consent screen's publishing status is *Testing*,
Google expires the sign-in after 7 days and the calendar shows "Sign in again". To avoid this,
choose **Publish app** on the consent screen. Google then shows a "Google hasn't verified this app"
warning when you sign in; because it's your own app, choose *Advanced → Go to Nagster*.
Verification by Google is only needed for apps used by more than 100 people.

## Microsoft 365 (work or school) account

1. Open [Microsoft Entra → App registrations](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
   and choose **New registration**.
2. Name it "Nagster". Under *Supported account types*, choose accounts in **this
   organizational directory only** (or *any organizational directory* if you'll connect accounts
   from several organisations).
3. Under *Redirect URI*, choose **Public client/native (mobile & desktop)** and enter
   `http://localhost`. Register.
4. Copy the **Application (client) ID** into the app's *App registrations* section. If you chose a
   single organisation, also copy the **Directory (tenant) ID** into *Tenant*.
5. In *API permissions*, check **Microsoft Graph → Calendars.Read (Delegated)** is listed (add it if
   not). No client secret is needed.
6. Choose **+ Microsoft 365 account** and sign in in the browser.

If sign-in says an administrator must approve the app, your organisation doesn't allow users to
consent to apps themselves; ask IT to grant consent for `Calendars.Read`.

## Outlook on this PC

Choose **+ Outlook on this PC**. The app reads calendars from **classic** desktop Outlook through
its automation interface, so any account Outlook shows works with no extra sign-in. Turn on
*Include all my calendars* to read every account's main calendar.

- The **new Outlook for Windows** has no automation interface and can't be read; use a Microsoft
  365 account or calendar link instead.
- If Outlook isn't running, it's started in the background for each check.
- Outlook may warn that "a program is trying to access your information" if your antivirus isn't
  recognised as up to date. The app only reads titles, times, locations and status (never the
  event body), which keeps such prompts rare.

## What gets imported

- Events starting within the look-ahead window (7 days by default), checked every 15 minutes and
  whenever you save the Calendars window.
- Each alarm is an event reminder that goes off the chosen number of minutes before the start
  (10 by default). All-day events, if not skipped, remind at 9:00 AM on the day.
- **Never duplicated:** an event is recognised by its calendar ID (which survives it being moved)
  and by its title and start time, so the same meeting arriving through two services, such as
  Google and Outlook, becomes one alarm.
- **Skipped:** cancelled events, events already under way, and anything matching your settings:
  all-day events, meetings you've declined, events shown as free, and **ignore rules** such as
  *Title is Dance* (matching ignores upper and lower case).

Use **Check** on a calendar to see what would be imported, and why other events were skipped,
without creating any alarms.

Sign-in tokens are kept in `%LOCALAPPDATA%\Nagster\Tokens`, encrypted for your Windows
account. Removing a calendar deletes its tokens.
