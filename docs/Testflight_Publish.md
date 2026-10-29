# Publishing a new iPhone build to TestFlight

The **TestFlight** workflow (`.github/workflows/testflight.yml`) builds the iPhone app, signs it and
uploads it to App Store Connect. It only runs when you press **Run workflow** on GitHub; pushing
commits or tags doesn't start it.

The one-off setup (Apple Developer account, app record, API key and repository secrets) is in the
[iOS README](../ios/README.md#testflight-paid-apple-developer-account). Without the secrets the
workflow skips the upload and says so.

## Version and build numbers

- **Version** is what TestFlight shows, e.g. 0.2.0. Run on a tag `ios-v0.2.0`, it's 0.2.0. Run on a
  branch, the build uses `MARKETING_VERSION` in `ios/project.yml`.
- **Build number** is set automatically from the workflow's run number, so it always goes up. You
  never need to change it.
- Several builds can share a version while testing. Use a new version for each release you want to
  tell apart, and a higher one than any version already released on the App Store.

## Run workflow

1. On GitHub, open **Actions → TestFlight**.
2. Click **Run workflow**, choose the branch (normally `main`) or a version tag, and click **Run
   workflow**.

The button only appears for workflows on the default branch (`main`). Run on a branch, the version
comes from `MARKETING_VERSION` in `ios/project.yml`, so update that first if you want a new version.

To set the version with a tag instead, tag the commit you want to publish, then choose the tag in
step 2:

```sh
git checkout main
git pull
git tag -a ios-v0.2.0 -m "iPhone app 0.2.0"
git push origin ios-v0.2.0
```

The tag must start with `ios-v`. Pushing it doesn't start a build by itself.

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
| "Missing required icon file" or `CFBundleIconName`            | Run `windows/tools/CreateIosIcon.ps1` to recreate the icon, then publish with a new tag     |
| A fix you pushed isn't in the rebuilt app                     | Re-running a job builds the tag's original commit; publish the fix with a new tag           |
| "Your account has reached the maximum number of certificates" | Revoke unused "Created via API" Apple Development certificates in the developer portal      |
