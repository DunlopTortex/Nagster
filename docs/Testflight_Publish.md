# Publishing a new iPhone build to TestFlight

The **TestFlight** workflow (`.github/workflows/testflight.yml`) builds the iPhone app, signs it and
uploads it to App Store Connect. It only runs when you start it with **Run workflow** on GitHub;
pushing commits or tags never starts it.

The one-off setup (Apple Developer account, app record, API key and repository secrets) is in the
[iOS README](../ios/README.md#testflight-paid-apple-developer-account). Without the secrets the
workflow skips the upload and says so.

## Version and build numbers

- **Version** is what TestFlight shows, e.g. 0.2.0. Type it into the **version** box when you start
  the run. Left blank, the build uses `MARKETING_VERSION` in `ios/project.yml`.
- **Build number** is set automatically from the workflow's run number, so it always goes up. You
  never need to change it.
- Several builds can share a version while testing. Use a new version for each release you want to
  tell apart, and a higher one than any version already released on the App Store.

## Starting a build

1. On GitHub, open **Actions → TestFlight**.
2. Click **Run workflow**, choose the branch (normally `main`), type the version (e.g. `0.2.0`) or
   leave it blank, and click **Run workflow**.

The button only appears for workflows on the default branch (`main`). If you'd rather keep a git tag
per release, create it as usual (e.g. `ios-v0.2.0`), then pick the tag instead of a branch in the
**Run workflow** menu; with the version box blank, the tag sets the version.

## After starting it

1. Watch the run under **Actions → TestFlight**. It takes about 10–15 minutes, plus any wait for a
   macOS runner.
2. When it finishes, App Store Connect processes the build for another 10–30 minutes. Apple emails
   you when it's ready.
3. Open the **TestFlight** app on the iPhone and tap **Update** (or **Install**) on Nagster.

TestFlight builds stay installable for 90 days.

## If it fails

| What you see                                                  | What to do                                                                                  |
| ------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| "TestFlight secrets aren't set up yet"                        | Add the four secrets in **Settings → Secrets and variables → Actions** (see the iOS README) |
| Signing or provisioning errors in **Archive**                 | Check `APPLE_TEAM_ID` and the API key secrets are right                                     |
| "Cloud signing permission error" in **Upload**                | The API key needs the **Admin** role; create a new one and update its two secrets           |
| "No suitable application records were found"                  | The App Store Connect app's bundle ID must match `com.dgutilities.nagster`                  |
| "The bundle version must be higher"                           | Run it again; the new run number is higher                                                  |
| The version is closed for new builds                          | That version is already released or in review; publish with a higher version                |
| The job waits a long time, then is cancelled                  | GitHub had no macOS runner free; re-run it from the run's page                              |
| "Missing required icon file" or `CFBundleIconName`            | Run `windows/tools/CreateIosIcon.ps1` to recreate the icon, then start a new run            |
| A fix you pushed isn't in the rebuilt app                     | Re-running a job builds the original commit; start a new run with **Run workflow** instead  |
| "Your account has reached the maximum number of certificates" | Revoke unused "Created via API" Apple Development certificates in the developer portal      |
