# Publishing a new iPhone build to TestFlight

The **TestFlight** workflow (`.github/workflows/testflight.yml`) builds the iPhone app, signs it and
uploads it to App Store Connect. Start it in one of two ways: push a version tag, or press **Run
workflow** on GitHub.

The one-off setup (Apple Developer account, app record, API key and repository secrets) is in the
[iOS README](../ios/README.md#testflight-paid-apple-developer-account). Without the secrets the
workflow skips the upload and says so.

## Version and build numbers

- **Version** is what TestFlight shows, e.g. 0.2.0. A tag `ios-v0.2.0` sets it to 0.2.0. Run from the
  button, the build uses `MARKETING_VERSION` in `ios/project.yml`.
- **Build number** is set automatically from the workflow's run number, so it always goes up. You
  never need to change it.
- Several builds can share a version while testing. Use a new version for each release you want to
  tell apart, and a higher one than any version already released on the App Store.

## Option 1: push a tag

Tag the commit you want to publish, normally the latest on `main`:

```sh
git checkout main
git pull
git tag -a ios-v0.2.0 -m "iPhone app 0.2.0"
git push origin ios-v0.2.0
```

The tag must start with `ios-v`. Each tag can be used only once; if a build from a tag fails and you
fix it with a new commit, publish with the next version (e.g. `ios-v0.2.1`).

You can also create the tag on GitHub: **Releases → Draft a new release → Choose a tag**, type the
new tag name (e.g. `ios-v0.2.0`), choose the target branch, and click **Publish release**.

## Option 2: Run workflow

1. On GitHub, open **Actions → TestFlight**.
2. Click **Run workflow**, choose the branch (normally `main`) and click **Run workflow**.

The button only appears for workflows on the default branch (`main`). The version comes from
`MARKETING_VERSION` in `ios/project.yml`, so update that first if you want a new version.

## After starting it

1. Watch the run under **Actions → TestFlight**. It takes about 10–15 minutes, plus any wait for a
   macOS runner.
2. When it finishes, App Store Connect processes the build for another 10–30 minutes. Apple emails
   you when it's ready.
3. Open the **TestFlight** app on the iPhone and tap **Update** (or **Install**) on Nagster.

TestFlight builds stay installable for 90 days.

## If it fails

| What you see                                     | What to do                                                                                  |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| "TestFlight secrets aren't set up yet"           | Add the four secrets in **Settings → Secrets and variables → Actions** (see the iOS README) |
| Signing or provisioning errors in **Archive**    | Check the API key has the **App Manager** role and `APPLE_TEAM_ID` is right                 |
| "No suitable application records were found"     | The App Store Connect app's bundle ID must match `com.dgutilities.nagster`                  |
| "The bundle version must be higher"              | Run it again; the new run number is higher                                                  |
| The version is closed for new builds             | That version is already released or in review; publish with a higher version                |
| The job waits a long time, then is cancelled     | GitHub had no macOS runner free; re-run it from the run's page                              |
