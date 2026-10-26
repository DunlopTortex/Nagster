using Nagster;
using Nagster.Notifications;
using Nagster.Sounds;
using System.IO;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
}

var snoozeStart = new DateTime(2026, 1, 31, 9, 0, 0);
Check(SnoozeDuration.TryParseList("15, 30s, 2H, 1 d, 1w, 1mo, 1y") is { Count: 7 } parsed &&
      parsed.Select(d => d.AddTo(snoozeStart)).SequenceEqual(new[]
      {
          snoozeStart.AddMinutes(15), snoozeStart.AddSeconds(30), snoozeStart.AddHours(2), snoozeStart.AddDays(1),
          snoozeStart.AddDays(7), new DateTime(2026, 2, 28, 9, 0, 0), snoozeStart.AddYears(1)
      }), "snooze units parse and apply");
Check(SnoozeDuration.TryParseList("5, 0m") is null && SnoozeDuration.TryParseList("5x") is null &&
      SnoozeDuration.TryParseList("-5m") is null && SnoozeDuration.TryParseList(" , ") is null, "invalid snooze choices rejected");
Check(SnoozeDuration.TryParse("15", out var bare) && bare.ToString() == "15m" && bare.Label == "15 minutes" &&
      SnoozeDuration.TryParse("1mo", out var month) && month.Label == "1 month", "snooze choices normalise and label");
Check(new AlarmOccurrence { AlertAt = snoozeStart }.SnoozeStart(snoozeStart.AddHours(1)) == snoozeStart.AddHours(1) &&
      new AlarmOccurrence { AlertAt = snoozeStart.AddHours(1) }.SnoozeStart(snoozeStart) == snoozeStart.AddHours(1),
      "snooze counts from the later of now and the alert time");

var legacySettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AutoSnoozeMinutes\":45}")!;
var legacyAlarm = System.Text.Json.JsonSerializer.Deserialize<AlarmDefinition>("{\"AutoSnoozeMinutesOverride\":90}")!;
Check(legacySettings.AutoSnooze == "45m" && legacyAlarm.AutoSnoozeOverride == "90m" &&
      !System.Text.Json.JsonSerializer.Serialize(legacySettings).Contains("AutoSnoozeMinutes") &&
      !System.Text.Json.JsonSerializer.Serialize(legacyAlarm).Contains("AutoSnoozeMinutesOverride"), "legacy auto-snooze minutes migrate");

foreach (var choice in SoundLibrary.BuiltIn)
{
    using var stream = SoundLibrary.Open(choice.Reference);
    var pcm = new byte[stream!.Length];
    stream.ReadExactly(pcm);
    var samples = Enumerable.Range(0, pcm.Length / 2).Select(i => Math.Abs((int)BitConverter.ToInt16(pcm, 2 * i))).ToArray();
    Check(stream.TotalTime.TotalSeconds is > 1.5 and < 3.5 && samples.Max() is > 16000 and < 32000,
        $"built-in sound '{choice.Name}' renders audibly without clipping");
}
Check(SoundLibrary.IsAvailable("builtin:chime") && !SoundLibrary.IsAvailable("builtin:nope") &&
      !SoundLibrary.IsAvailable(@"windows:..\notepad") && !SoundLibrary.IsAvailable("") &&
      !SoundLibrary.IsAvailable(@"C:\no\such\file.wav"), "sound references validate");
Check(SoundLibrary.WindowsSounds().All(c => SoundLibrary.IsAvailable(c.Reference)) &&
      SoundLibrary.Describe("windows:Alarm03")?.Name == "Alarm 3" && SoundLibrary.Describe(@"D:\Tunes\Wake up.mp3")?.Name == "Wake up",
    "Windows sounds and file names describe");
var legacySound = System.Text.Json.JsonSerializer.Deserialize<AlarmDefinition>("""{"SoundPath":"C:\\a.wav"}""")!;
var legacyDefault = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"DefaultSoundPath":"builtin:beep"}""")!;
Check(legacySound.Sound == @"C:\a.wav" && legacyDefault.DefaultSound == "builtin:beep", "saved sound paths still load");

Check(new AppSettings().AlarmVolumePercent is null && VolumeOverride.Apply("", null) is null &&
      SoundOptions.From(new AppSettings { DefaultSound = "builtin:siren", AudioDeviceName = "Desk", AlarmVolumePercent = 70 })
          is { DefaultSound: "builtin:siren", DeviceName: "Desk", VolumePercent: 70 }, "alarm volume is opt-in and flows into sound options");

var overnight = new QuietHours { Enabled = true, Start = new TimeSpan(22, 0, 0), End = new TimeSpan(7, 0, 0) };
var daytime = new QuietHours { Enabled = true, Start = new TimeSpan(12, 0, 0), End = new TimeSpan(13, 30, 0) };
DateTime At(int hour, int minute = 0) => new(2026, 10, 16, hour, minute, 0);
Check(overnight.Contains(At(23)) && overnight.Contains(At(3)) && overnight.Contains(At(22)) &&
      !overnight.Contains(At(7)) && !overnight.Contains(At(12)), "overnight quiet hours wrap past midnight");
Check(daytime.Contains(At(12)) && daytime.Contains(At(13, 29)) && !daytime.Contains(At(13, 30)) && !daytime.Contains(At(11, 59)),
    "same-day quiet hours include start and exclude end");
Check(!new QuietHours { Enabled = false }.Contains(At(23)) &&
      !new QuietHours { Enabled = true, Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(9) }.Contains(At(9)),
    "disabled or empty quiet hours never silence");
var savedQuiet = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(new AppSettings { QuietHours = overnight }))!;
Check(savedQuiet.QuietHours.Enabled && savedQuiet.QuietHours.Start == overnight.Start && savedQuiet.QuietHours.End == overnight.End &&
      SoundOptions.From(savedQuiet).IsQuietAt(At(2)) && !SoundOptions.From(new AppSettings()).IsQuietAt(At(2)),
    "quiet hours save, load and reach sound playback");

var protectedKey = SecretProtector.Protect("uQiRzpo4DXghDmr9QzzfQu27cmVRsG");
Check(protectedKey.Length > 0 && !protectedKey.Contains("uQiRzpo4") &&
      SecretProtector.Unprotect(protectedKey) == "uQiRzpo4DXghDmr9QzzfQu27cmVRsG" &&
      SecretProtector.Unprotect("not-base64!") == "" && SecretProtector.Protect("") == "", "Pushover keys are encrypted and round-trip");

var renameRoot = Path.Combine(Path.GetTempPath(), "Nagster-rename-" + Guid.NewGuid());
try
{
    var legacyFolder = Directory.CreateDirectory(Path.Combine(renameRoot, "DGNaggingAlarms")).FullName;
    File.WriteAllText(Path.Combine(legacyFolder, "alarms.db"), "old data");
    var moved = AppPaths.ResolveDataFolder(renameRoot);
    Check(moved == Path.Combine(renameRoot, "Nagster") && File.ReadAllText(Path.Combine(moved, "alarms.db")) == "old data" &&
          !Directory.Exists(legacyFolder), "data from before the rename moves to the Nagster folder");
    Directory.CreateDirectory(legacyFolder);
    Check(AppPaths.ResolveDataFolder(renameRoot) == moved && File.Exists(Path.Combine(moved, "alarms.db")),
        "an existing Nagster folder is never replaced by a leftover legacy one");
}
finally { Directory.Delete(renameRoot, true); }

var noticePath = Path.Combine(Path.GetTempPath(), "Nagster-notice-" + Guid.NewGuid() + ".db");
try
{
    var noticeEngine = new AlarmEngine(new AlarmStore(noticePath));
    var unanswered = new AlarmDefinition { Title = "Water plants", FirstDue = DateTime.Now.AddMinutes(5) };
    noticeEngine.Add(unanswered);
    var morning = DateTime.Today.AddHours(8);
    Check(noticeEngine.TryClaimDailyNotice(unanswered, morning) && !noticeEngine.TryClaimDailyNotice(unanswered, morning.AddHours(6)) &&
          noticeEngine.TryClaimDailyNotice(unanswered, morning.AddDays(1)), "Pushover notice sent once per alarm per day");
    var reloaded = new AlarmEngine(new AlarmStore(noticePath));
    Check(!reloaded.TryClaimDailyNotice(reloaded.State.Definitions.Single(), morning.AddDays(1).AddHours(2)),
        "daily Pushover notice survives restart");
}
finally { if (File.Exists(noticePath)) File.Delete(noticePath); }

SyncChecks.Run(Check);
CloudChecks.Run(Check);
LocalApiChecks.Run(Check);

// ---- Web editing ----
var webPath = Path.Combine(Path.GetTempPath(), "Nagster-web-" + Guid.NewGuid() + ".db");
try
{
    var webEngine = new AlarmEngine(new AlarmStore(webPath));
    var walkStart = DateTime.Today.AddDays(1).AddHours(9);
    var walk = new AlarmDefinition
    {
        Title = "Walk", FirstDue = walkStart, Repeat = new RepeatRule { Kind = RepeatKind.Daily, Count = 20 },
        Sound = "builtin:chime", DeadlineAfterMinutes = 30
    };
    webEngine.Add(walk);
    webEngine.Ready(DateTime.Now);
    string Local(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm");
    List<AlarmOccurrence> Walks() => webEngine.State.Occurrences.Where(o => o.DefinitionId == walk.Id).OrderBy(o => o.ScheduledFor).ToList();
    var second = Walks()[1];
    var details = Nagster.Web.WebApi.Details(webEngine, second, DateTime.Now);
    Check(details is { Recurring: true, Repeat: "Daily", Interval: 1, DeadlineMinutes: 30 } &&
          details.When == Local(walkStart.AddDays(1)) && details.SeriesWhen == Local(walkStart),
        "web edit details show this occurrence and where the series restarts");

    Check(Nagster.Web.WebApi.Edit(webEngine, second, new Nagster.Web.EditAlarmRequest(null, "Walk", "", Local(walkStart), "Daily"))
          is not null, "editing a repeating alarm from the web needs a scope");

    var moved = walkStart.AddDays(1).AddHours(2);
    Check(Nagster.Web.WebApi.Edit(webEngine, second,
              new Nagster.Web.EditAlarmRequest("one", "Long walk", "Park", Local(moved), "Daily", 1, 45)) is null &&
          webEngine.State.Definitions.Single(d => d.Id != walk.Id) is { Title: "Long walk", Sound: "builtin:chime", DeadlineAfterMinutes: 45 } single &&
          single.Repeat.Kind == RepeatKind.Once && webEngine.State.Occurrences.Single(o => o.DefinitionId == single.Id).ScheduledFor == moved &&
          Walks().All(o => o.ScheduledFor != walkStart.AddDays(1)) && Walks()[0].ScheduledFor == walkStart && walk.Title == "Walk",
        "editing just one occurrence moves it into a separate one-off alarm");

    var oneOff = webEngine.State.Definitions.Single(d => d.Id != walk.Id);
    var seriesProblem = Nagster.Web.WebApi.Edit(webEngine, Walks()[0],
        new Nagster.Web.EditAlarmRequest("all", "Walk the dog", "", Local(walkStart.AddHours(1)), "Daily", 2));
    webEngine.Ready(DateTime.Now);
    Check(seriesProblem is null &&
          webEngine.State.Definitions.Single(d => d.Id == walk.Id) is { Title: "Walk the dog", Sound: "builtin:chime", DeadlineAfterMinutes: null } series &&
          series.Repeat is { Kind: RepeatKind.Daily, Interval: 2, Count: 20 } && Walks()[0].ScheduledFor == walkStart.AddHours(1) &&
          Walks()[1].ScheduledFor == walkStart.AddDays(2).AddHours(1) && webEngine.State.Definitions.Contains(oneOff),
        "editing the whole series keeps unseen settings and leaves detached occurrences alone");
}
finally { if (File.Exists(webPath)) File.Delete(webPath); }

var body = AutoSnoozeNotifier.Body(
    new AlarmDefinition { Title = "Call Sam", Description = "About the lease" },
    new AlarmOccurrence { ScheduledFor = DateTime.Today.AddHours(9), Deadline = DateTime.Today.AddHours(17) },
    DateTime.Today.AddHours(10));
Check(body.Contains("\"Call Sam\"") && body.Contains("wasn't answered") && body.Contains("Must be completed by") &&
      body.Contains("About the lease") && AutoSnoozeNotifier.Title(new AlarmDefinition { Title = "Call Sam" }) == "Unanswered: Call Sam",
    "Pushover message describes the unanswered alarm");

// ---- Calendar import ----
var icsDay = DateTime.Today.AddDays(2);
string IcsLocal(DateTime value) => value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
var ics = string.Join("\r\n",
    "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//EN", "X-WR-CALNAME:Family",
    "BEGIN:VEVENT", "UID:standup@test", $"DTSTART:{IcsLocal(icsDay.AddHours(9))}", $"DTEND:{IcsLocal(icsDay.AddHours(9.5))}",
    "RRULE:FREQ=DAILY;COUNT=4", $"EXDATE:{IcsLocal(icsDay.AddDays(2).AddHours(9))}", "SUMMARY:Stand-up", "END:VEVENT",
    // The second occurrence moved from 09:00 to 11:00.
    "BEGIN:VEVENT", "UID:standup@test", $"RECURRENCE-ID:{IcsLocal(icsDay.AddDays(1).AddHours(9))}",
    $"DTSTART:{IcsLocal(icsDay.AddDays(1).AddHours(11))}", $"DTEND:{IcsLocal(icsDay.AddDays(1).AddHours(11.5))}", "SUMMARY:Stand-up (moved)", "END:VEVENT",
    "BEGIN:VEVENT", "UID:dance@test", $"DTSTART:{IcsLocal(icsDay.AddHours(18))}", $"DTEND:{IcsLocal(icsDay.AddHours(19))}",
    "SUMMARY:Dance", "LOCATION:Town hall", "END:VEVENT",
    "BEGIN:VEVENT", "UID:holiday@test", $"DTSTART;VALUE=DATE:{icsDay:yyyyMMdd}", $"DTEND;VALUE=DATE:{icsDay.AddDays(1):yyyyMMdd}", "SUMMARY:Holiday", "END:VEVENT",
    "BEGIN:VEVENT", "UID:focus@test", $"DTSTART:{IcsLocal(icsDay.AddHours(14))}", $"DTEND:{IcsLocal(icsDay.AddHours(15))}",
    "SUMMARY:Focus time", "TRANSP:TRANSPARENT", "END:VEVENT",
    "BEGIN:VEVENT", "UID:off@test", $"DTSTART:{IcsLocal(icsDay.AddHours(16))}", $"DTEND:{IcsLocal(icsDay.AddHours(17))}",
    "SUMMARY:Called off", "STATUS:CANCELLED", "END:VEVENT",
    "END:VCALENDAR", "");
var icsEvents = Nagster.Calendars.IcsCalendarProvider.Parse(ics, "Fallback", DateTime.Now, DateTime.Now.AddDays(14));
var standups = icsEvents.Where(e => e.Title.StartsWith("Stand-up")).OrderBy(e => e.Start).ToList();
Check(standups.Count == 3 && standups[1].Title == "Stand-up (moved)" && standups[1].Start == icsDay.AddDays(1).AddHours(11) &&
      standups[1].Id == Nagster.Calendars.EventIds.FromUid("standup@test", icsDay.AddDays(1).AddHours(9)) &&
      standups.All(s => s.CalendarName == "Family"),
    "ICS repeats expand with EXDATE skipped and a moved occurrence keeping its original identity");
Check(icsEvents.Single(e => e.Title == "Holiday").AllDay && icsEvents.Single(e => e.Title == "Focus time").Free &&
      icsEvents.Single(e => e.Title == "Called off").Cancelled && icsEvents.Single(e => e.Title == "Dance").Id == "uid:dance@test",
    "ICS all-day, free, cancelled and one-off events are recognised");
Check(Nagster.Calendars.IcsCalendarProvider.NormaliseUrl("webcal://calendar.example.com/a.ics")?.Scheme == "https" &&
      Nagster.Calendars.IcsCalendarProvider.NormaliseUrl("not a link") is null, "webcal links are accepted as https");

var family = new Nagster.Calendars.CalendarSource { Name = "Family", LeadMinutes = 15 };
var syncSettings = new Nagster.Calendars.CalendarSyncSettings
{
    Rules = [new Nagster.Calendars.IgnoreRule { Field = Nagster.Calendars.RuleField.Title, Match = Nagster.Calendars.RuleMatch.Is, Text = "dance" }]
};
var plans = Nagster.Calendars.ImportPlanner.Plan(icsEvents, family, syncSettings, new HashSet<string>(), DateTime.Now);
Nagster.Calendars.ImportDecision DecisionFor(string title) => plans.First(p => p.Event.Title == title).Decision;
Check(DecisionFor("Dance") == Nagster.Calendars.ImportDecision.Rule && DecisionFor("Holiday") == Nagster.Calendars.ImportDecision.AllDay &&
      DecisionFor("Called off") == Nagster.Calendars.ImportDecision.Cancelled && DecisionFor("Focus time") == Nagster.Calendars.ImportDecision.Import &&
      plans.Count(p => p.Decision == Nagster.Calendars.ImportDecision.Import) == 4,
    "rules skip \"Title is Dance\" (any case), all-day and cancelled events");
var firstStandup = plans.First(p => p.Decision == Nagster.Calendars.ImportDecision.Import && p.Event.Title == "Stand-up").Alarm!;
Check(firstStandup.IsEvent && firstStandup.ReminderMinutes == 15 && firstStandup.ImportedFrom == "Family" &&
      firstStandup.FirstDue == icsDay.AddHours(9) && firstStandup.EventEnd == icsDay.AddHours(9.5),
    "imported alarm is an event reminder with the calendar's lead time");
var freeSkipped = Nagster.Calendars.ImportPlanner.Plan(icsEvents, family, new Nagster.Calendars.CalendarSyncSettings { SkipFree = true }, new HashSet<string>(), DateTime.Now);
Check(freeSkipped.First(p => p.Event.Title == "Focus time").Decision == Nagster.Calendars.ImportDecision.Free &&
      new Nagster.Calendars.IgnoreRule { Field = Nagster.Calendars.RuleField.Location, Match = Nagster.Calendars.RuleMatch.Contains, Text = "TOWN" }
          .Matches(icsEvents.Single(e => e.Title == "Dance")),
    "skip-free setting and location rules apply");

// The same meeting from another service has a different ID but the same title and start.
var fromOutlook = new Nagster.Calendars.CalendarEvent("outlook:XYZ", "Focus Time", "", "", icsDay.AddHours(14), icsDay.AddHours(15), false, false, false, false, "Calendar");
var importPath = Path.Combine(Path.GetTempPath(), "Nagster-import-" + Guid.NewGuid() + ".db");
try
{
    var importEngine = new AlarmEngine(new AlarmStore(importPath));
    var created = importEngine.Import(plans, DateTime.Now);
    var replan = Nagster.Calendars.ImportPlanner.Plan(icsEvents.Append(fromOutlook), family, syncSettings, importEngine.ImportedEventKeys(), DateTime.Now);
    Check(created == 4 && importEngine.State.Definitions.Count == 4 &&
          replan.Count(p => p.Decision == Nagster.Calendars.ImportDecision.Import) == 0 &&
          replan.Single(p => p.Event == fromOutlook).Decision == Nagster.Calendars.ImportDecision.AlreadyImported,
        "a second sync, and the same event from another service, create no duplicates");
    importEngine.Delete(importEngine.State.Definitions.First().Id);
    var afterRestart = new AlarmEngine(new AlarmStore(importPath));
    Check(Nagster.Calendars.ImportPlanner.Plan(icsEvents, family, syncSettings, afterRestart.ImportedEventKeys(), DateTime.Now)
              .All(p => p.Decision != Nagster.Calendars.ImportDecision.Import),
        "a deleted imported alarm isn't recreated, even after a restart");
    var twice = Nagster.Calendars.ImportPlanner.Plan(icsEvents.Concat(icsEvents), family, syncSettings, new HashSet<string>(), DateTime.Now);
    Check(twice.Count(p => p.Decision == Nagster.Calendars.ImportDecision.Import) == 4, "an event listed twice in one sync is imported once");
}
finally { if (File.Exists(importPath)) File.Delete(importPath); }

// ---- Editing alarms ----
var editPath = Path.Combine(Path.GetTempPath(), "Nagster-edit-" + Guid.NewGuid() + ".db");
try
{
    var editEngine = new AlarmEngine(new AlarmStore(editPath));
    var daily = new AlarmDefinition
    {
        Title = "Vitamins", FirstDue = DateTime.Now.AddMinutes(-30), ImportedFrom = "Family",
        Repeat = new RepeatRule { Kind = RepeatKind.Daily, Interval = 1, Count = 5 }
    };
    editEngine.Add(daily);
    editEngine.Ready(DateTime.Now);                           // materialises the remaining four
    var past = editEngine.State.Occurrences.First(o => o.DefinitionId == daily.Id);
    editEngine.Complete(past);                                // history to keep
    var newStart = DateTime.Today.AddDays(1).AddHours(8);
    editEngine.Update(daily.Id, new AlarmDefinition
    {
        Title = "Vitamins with breakfast", FirstDue = newStart,
        Repeat = new RepeatRule { Kind = RepeatKind.Daily, Interval = 1, Count = 5 }
    });
    editEngine.Ready(DateTime.Now);
    var series = editEngine.State.Occurrences.Where(o => o.DefinitionId == daily.Id).OrderBy(o => o.ScheduledFor).ToList();
    var edited = editEngine.State.Definitions.Single();
    Check(edited.Id == daily.Id && edited.Title == "Vitamins with breakfast" && edited.ImportedFrom == "Family" &&
          series.Count == 5 && series[0].Status == OccurrenceStatus.Completed && series[1].ScheduledFor == newStart &&
          series.Skip(1).All(o => o.Status == OccurrenceStatus.Pending && o.ScheduledFor.TimeOfDay == newStart.TimeOfDay),
        "editing keeps history and identity, reschedules upcoming occurrences, and still stops after 5 in total");
    var reloadedEdit = new AlarmEngine(new AlarmStore(editPath));
    Check(reloadedEdit.State.Definitions.Single().Title == "Vitamins with breakfast" &&
          reloadedEdit.NextPending(daily.Id)?.ScheduledFor == newStart, "edits are saved");
}
finally { if (File.Exists(editPath)) File.Delete(editPath); }

var pipeName = "Nagster.Check." + Guid.NewGuid().ToString("N");
using (var pipeServer = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.In))
using (var pipeClient = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.Out))
{
    var accepted = pipeServer.WaitForConnectionAsync();
    pipeClient.Connect(3000);
    accepted.Wait(3000);
    Check(Foreground.ServerProcessId(pipeClient) == Environment.ProcessId, "a second launch can find the running app's process to hand it the foreground");
}

// ---- Snoozed list on the Active alarms screen ----
var snoozedPath = Path.Combine(Path.GetTempPath(), "Nagster-snoozed-" + Guid.NewGuid() + ".db");
try
{
    var snoozeEngine = new AlarmEngine(new AlarmStore(snoozedPath));
    AlarmOccurrence Due(string title)
    {
        snoozeEngine.Add(new AlarmDefinition { Title = title, FirstDue = DateTime.Now.AddMinutes(-2) });
        return snoozeEngine.State.Occurrences.Last();
    }
    var ringingNow = Due("Ringing");
    var byUser = Due("Snoozed by user");
    var automatic = Due("Auto-snoozed");
    var dueAgain = Due("Snooze ended");
    var finished = Due("Done");
    snoozeEngine.TrySnooze(byUser, DateTime.Now.AddMinutes(30), false, out _);
    snoozeEngine.TrySnooze(automatic, DateTime.Now.AddMinutes(10), true, out _);
    snoozeEngine.TrySnooze(dueAgain, DateTime.Now.AddMinutes(5), false, out _);
    dueAgain.AlertAt = DateTime.Now.AddMinutes(-1);            // its snooze has run out
    snoozeEngine.Complete(finished);
    var snoozedList = snoozeEngine.Snoozed(DateTime.Now);
    var ringingList = snoozeEngine.Ringing(DateTime.Now);
    string By(AlarmOccurrence o) => new AlarmRow(o, snoozeEngine.Definition(o)).SnoozedBy;
    Check(snoozedList.Select(o => snoozeEngine.Definition(o).Title).SequenceEqual(["Auto-snoozed", "Snoozed by user"]) &&
          ringingList.Any(o => o == ringingNow) && ringingList.Any(o => o == dueAgain) && !ringingList.Any(o => o == byUser),
        "snoozed alarms waiting to go off again are listed soonest first; ones whose snooze ran out are going off");
    Check(By(automatic) == "Automatically" && By(byUser) == "You" && new AlarmRow(automatic, snoozeEngine.Definition(automatic)).SnoozeCount == 1,
        "the snoozed list shows who snoozed each alarm and how many times");
}
finally { if (File.Exists(snoozedPath)) File.Delete(snoozedPath); }

// ---- Pausing alarms ----
var pausePath = Path.Combine(Path.GetTempPath(), "Nagster-pause-" + Guid.NewGuid() + ".db");
try
{
    var pauseEngine = new AlarmEngine(new AlarmStore(pausePath));
    pauseEngine.Add(new AlarmDefinition { Title = "On screen", FirstDue = DateTime.Now.AddMinutes(-1) });
    var onScreen = pauseEngine.State.Occurrences.Last();
    pauseEngine.Activate(onScreen);
    pauseEngine.Add(new AlarmDefinition { Title = "Due during pause", FirstDue = DateTime.Now.AddMinutes(20) });
    var later = pauseEngine.State.Occurrences.Last();
    var pauseNow = DateTime.Now;
    pauseEngine.Pause(pauseNow.AddMinutes(30));
    Check(pauseEngine.IsPaused && !pauseEngine.PauseExpired(pauseNow) && pauseEngine.PauseExpired(pauseNow.AddMinutes(31)) &&
          onScreen.Status == OccurrenceStatus.Pending,
        "pausing returns on-screen alarms to waiting, and the pause's time runs out at the chosen time");
    var afterRestart = new AlarmEngine(new AlarmStore(pausePath));
    Check(afterRestart.IsPaused && afterRestart.State.Settings.PausedUntil == pauseEngine.State.Settings.PausedUntil, "a pause survives a restart");
    Check(pauseEngine.IsPaused, "a pause whose time has run out stays paused until the user chooses");
    var whenPauseEnds = pauseNow.AddMinutes(31);
    var dueAfterPause = pauseEngine.Ready(whenPauseEnds);
    Check(dueAfterPause.Contains(onScreen) && dueAfterPause.Contains(later), "when a pause ends, everything that came due during it goes off");
    pauseEngine.Pause(DateTime.MaxValue);
    Check(pauseEngine.IsPaused && !pauseEngine.PauseExpired(DateTime.Now.AddYears(50)), "Forever never runs out");
    pauseEngine.Pause(DateTime.Now.Add(PauseEndedPopup.SnoozeLength));
    Check(pauseEngine.IsPaused && !pauseEngine.PauseExpired(DateTime.Now) && pauseEngine.PauseExpired(DateTime.Now.AddMinutes(31)),
        "snoozing a finished pause keeps alarms paused for 30 more minutes");
    pauseEngine.Resume();
    Check(!pauseEngine.IsPaused && new AlarmEngine(new AlarmStore(pausePath)).State.Settings.PausedUntil is null, "Unpause ends the pause and is saved");
}
finally { if (File.Exists(pausePath)) File.Delete(pausePath); }

// ---- Collapsing missed repeats ----
var collapsePath = Path.Combine(Path.GetTempPath(), "Nagster-collapse-" + Guid.NewGuid() + ".db");
try
{
    var collapseEngine = new AlarmEngine(new AlarmStore(collapsePath));
    var collapseNow = DateTime.Now;
    var hourly = new AlarmDefinition
    {
        Title = "Stretch", FirstDue = collapseNow.AddHours(-5).AddMinutes(-1), Repeat = new RepeatRule { Kind = RepeatKind.Hours, Interval = 1 }
    };
    collapseEngine.Add(hourly);
    collapseEngine.Add(new AlarmDefinition { Title = "One-off", FirstDue = collapseNow.AddMinutes(-30) });
    var collapseDue = collapseEngine.Ready(collapseNow);
    var stretch = collapseEngine.State.Occurrences.Where(o => o.DefinitionId == hourly.Id).OrderBy(o => o.ScheduledFor).ToList();
    var missedStretches = stretch.Where(o => o.LastAction == AlarmEngine.MissedAction).ToList();
    Check(collapseDue.Count(o => o.DefinitionId == hourly.Id) == 1 &&
          collapseDue.Single(o => o.DefinitionId == hourly.Id).ScheduledFor == stretch.Where(o => o.ScheduledFor <= collapseNow).Max(o => o.ScheduledFor) &&
          missedStretches.Count == 5 && missedStretches.All(o => o.Status == OccurrenceStatus.Dismissed) &&
          stretch.Where(o => o.ScheduledFor > collapseNow).All(o => o.Status == OccurrenceStatus.Pending),
        "a repeating alarm missed several times goes off once, for its latest missed time; earlier ones are marked missed");
    Check(collapseDue.Any(o => collapseEngine.Definition(o).Title == "One-off"), "different alarms due together each still go off");

    // An occurrence already on screen isn't replaced when the next one of the series comes due.
    var showing = collapseDue.Single(o => o.DefinitionId == hourly.Id);
    collapseEngine.Activate(showing);
    var nextHour = stretch.First(o => o.ScheduledFor > collapseNow);
    var laterDue = collapseEngine.Ready(nextHour.ScheduledFor.AddMinutes(1));
    Check(showing.Status == OccurrenceStatus.Active && laterDue.Contains(nextHour), "an alarm on screen is never marked missed");
    Check(new AlarmEngine(new AlarmStore(collapsePath)).State.Occurrences.Count(o => o.LastAction == AlarmEngine.MissedAction) == 5,
        "missed occurrences are saved, so they stay in the history");
}
finally { if (File.Exists(collapsePath)) File.Delete(collapsePath); }

// ---- Web sign-ins that survive restarts ----
var sessionsPath = Path.Combine(Path.GetTempPath(), "Nagster-sessions-" + Guid.NewGuid() + ".json");
try
{
    var iphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    var edge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0";
    var signIns = new Nagster.Web.WebSessions(sessionsPath);
    var phoneToken = signIns.Create(iphone);
    var pcToken = signIns.Create(edge);
    var reopened = new Nagster.Web.WebSessions(sessionsPath);
    Check(reopened.IsValid(phoneToken) && reopened.IsValid(pcToken) && !reopened.IsValid("made-up"),
        "web sign-ins survive the app restarting");
    Check(!File.ReadAllText(sessionsPath).Contains(phoneToken) && !File.ReadAllText(sessionsPath).Contains(pcToken),
        "the saved file holds only hashes, so it can't be used to sign in");
    Check(reopened.Devices().Select(d => d.Device).OrderBy(d => d).SequenceEqual(["iPhone · Safari", "Windows · Edge"]),
        "signed-in devices are listed by a recognisable name");
    reopened.End(pcToken);
    Check(!new Nagster.Web.WebSessions(sessionsPath).IsValid(pcToken) && new Nagster.Web.WebSessions(sessionsPath).IsValid(phoneToken),
        "signing out one device leaves the others signed in");
    // A device unused for longer than the session lifetime has to sign in again.
    var json = File.ReadAllText(sessionsPath);
    var stale = System.Text.RegularExpressions.Regex.Replace(json, "\"LastUsed\":\"[^\"]+\"",
        $"\"LastUsed\":\"{DateTime.UtcNow.AddDays(-200):O}\"");
    File.WriteAllText(sessionsPath, stale);
    Check(!new Nagster.Web.WebSessions(sessionsPath).IsValid(phoneToken), "a device unused for over 6 months must sign in again");
    var fresh = new Nagster.Web.WebSessions(sessionsPath);
    var again = fresh.Create(iphone);
    fresh.EndAll();
    Check(!new Nagster.Web.WebSessions(sessionsPath).IsValid(again), "Sign out all devices is saved too");
    File.WriteAllText(sessionsPath, "{ not json");
    Check(new Nagster.Web.WebSessions(sessionsPath).Devices().Count == 0, "a damaged sessions file just means signing in again");
}
finally { if (File.Exists(sessionsPath)) File.Delete(sessionsPath); }

var monday = new DateTime(2026, 10, 12, 9, 0, 0);
var weekly = new RepeatRule { Kind = RepeatKind.Weekly, Weekdays = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Friday) };
Check(weekly.Next(monday, monday) == new DateTime(2026, 10, 16, 9, 0, 0), "weekly selected weekdays");

var monthly = new RepeatRule { Kind = RepeatKind.Monthly, Nth = -1, NthWeekday = DayOfWeek.Friday };
Check(monthly.Next(new DateTime(2026, 9, 25, 9, 0, 0), new DateTime(2026, 9, 25, 9, 0, 0)) ==
      new DateTime(2026, 10, 30, 9, 0, 0), "last Friday of month");

var yearly = new RepeatRule { Kind = RepeatKind.Yearly, UseLastDayForShortMonths = true };
Check(yearly.Next(new DateTime(2028, 2, 29, 9, 0, 0), new DateTime(2028, 2, 29, 9, 0, 0)) ==
      new DateTime(2029, 2, 28, 9, 0, 0), "leap-day fallback");

var testPath = Path.Combine(Path.GetTempPath(), "Nagster-check-" + Guid.NewGuid() + ".db");
try
{
    var engine = new AlarmEngine(new AlarmStore(testPath));
    var definition = new AlarmDefinition
    {
        Title = "Check deadline", FirstDue = DateTime.Now.AddMinutes(5), DeadlineAfterMinutes = 30
    };
    engine.Add(definition);
    var occurrence = engine.State.Occurrences.Single();
    Check(!engine.TrySnooze(occurrence, DateTime.Now.AddHours(1), true, out _), "automatic snooze respects deadline");
    Check(engine.TrySnooze(occurrence, DateTime.Now.AddMinutes(10), false, out _), "valid snooze succeeds");
    var restored = new AlarmEngine(new AlarmStore(testPath));
    Check(restored.State.Occurrences.Single().SnoozeCount == 1, "snooze survives restart");
    restored.Add(new AlarmDefinition
    {
        Title = "Calendar meeting", IsEvent = true, FirstDue = DateTime.Now.AddHours(2),
        EventEnd = DateTime.Now.AddHours(3), ReminderMinutes = 15
    });
    var reminder = restored.State.Occurrences.Single(o => restored.Definition(o).IsEvent);
    Check(reminder.AlertAt == reminder.ScheduledFor.AddMinutes(-15), "event reminder precedes event start");
    var series = new AlarmDefinition
    {
        Title = "Three days", FirstDue = DateTime.Now.Date.AddDays(1).AddHours(9),
        Repeat = new RepeatRule { Kind = RepeatKind.Daily, Count = 3 }
    };
    restored.Add(series);
    restored.Ready(DateTime.Now);
    Check(restored.State.Occurrences.Count(o => o.DefinitionId == series.Id) == 3, "calendar materializes upcoming recurrence and respects count");
}
finally { if (File.Exists(testPath)) File.Delete(testPath); }

// Runs before any App object exists, so showing a window can't trigger the real app's startup.
var fitsScreen = false;
var placementThread = new Thread(() =>
{
    // Invisible, so the check doesn't flash a window on screen.
    var tall = new System.Windows.Window
    {
        Width = 300, Height = 20000, WindowStyle = System.Windows.WindowStyle.None, AllowsTransparency = true, Opacity = 0,
        ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
    };
    tall.Show();
    // Called directly rather than via the Loaded event, so the check needs no message loop.
    WindowPlacement.FitToScreen(tall);
    var workArea = System.Windows.SystemParameters.WorkArea; // The test runs on the primary screen.
    fitsScreen = (tall.Height <= workArea.Height + 1 && tall.Top >= workArea.Top - 1);
    tall.Close();
});
placementThread.SetApartmentState(ApartmentState.STA);
placementThread.Start();
placementThread.Join();
Check(fitsScreen, "a window too big for the screen is shrunk to fit and kept on screen");

Exception? uiError = null;
var durationSteps = new List<bool>();
var editSteps = new List<bool>();
var placementSteps = new List<bool>();
var pauseWording = "";
var uiPath = Path.Combine(Path.GetTempPath(), "Nagster-ui-check-" + Guid.NewGuid() + ".db");
var uiThread = new Thread(() =>
{
    try
    {
        var app = new App();
        app.InitializeComponent();
        typeof(App).GetProperty(nameof(App.Engine))!.SetValue(app, new AlarmEngine(new AlarmStore(uiPath)));
        _ = new MainWindow();
        _ = new SettingsWindow();
        _ = new AlarmCenterWindow();
        _ = new QuickReminderWindow();
        _ = new NewAlarmWindow();
        var original = new AlarmDefinition
        {
            Title = "Team stand-up", Description = "Daily sync", IsEvent = true, ReminderMinutes = 5,
            FirstDue = DateTime.Today.AddHours(9), EventEnd = DateTime.Today.AddHours(10.5),
            Repeat = new RepeatRule { Kind = RepeatKind.Weekly, Interval = 2, Weekdays = (1 << 1) | (1 << 3), Count = 12 },
            DeadlineAfterMinutes = 30, Sound = "builtin:chime", ResponseSecondsOverride = 45, AutoSnoozeOverride = "2h",
            Devices = AlarmDevices.Phone, Important = true
        };
        var nextRun = DateTime.Today.AddDays(2).AddHours(9);
        var form = new NewAlarmWindow(original, nextRun);
        T Find<T>(string name) => (T)form.FindName(name);
        var weekdays = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" }.Select(d => Find<System.Windows.Controls.CheckBox>(d).IsChecked == true).ToArray();
        editSteps.Add(form.Title == "Edit alarm" && (string)Find<System.Windows.Controls.Button>("SaveButton").Content == "Save changes");
        editSteps.Add(Find<System.Windows.Controls.TextBox>("TitleBox").Text == "Team stand-up" && Find<System.Windows.Controls.TextBox>("DescriptionBox").Text == "Daily sync" &&
                      Find<System.Windows.Controls.RadioButton>("TypeEvent").IsChecked == true);
        editSteps.Add(Find<System.Windows.Controls.DatePicker>("DueDate").SelectedDate == nextRun.Date &&
                      Find<Nagster.Controls.TimePicker>("DueTime").SelectedTime == nextRun.TimeOfDay);
        editSteps.Add(Find<System.Windows.Controls.DatePicker>("EventEndDate").SelectedDate == nextRun.Date &&
                      Find<Nagster.Controls.TimePicker>("EventEndTime").SelectedTime == TimeSpan.FromHours(10.5) &&
                      (string)((System.Windows.Controls.ComboBoxItem)Find<System.Windows.Controls.ComboBox>("EventDuration").SelectedItem).Content == "1.5 hours" &&
                      Find<System.Windows.Controls.TextBox>("ReminderMinutes").Text == "5");
        editSteps.Add(Find<System.Windows.Controls.ComboBox>("RepeatBox").SelectedIndex == (int)RepeatKind.Weekly && Find<System.Windows.Controls.TextBox>("IntervalBox").Text == "2" &&
                      weekdays.SequenceEqual(new[] { false, true, false, true, false, false, false }) && Find<System.Windows.Controls.TextBox>("RepeatCount").Text == "12");
        editSteps.Add(Find<System.Windows.Controls.ComboBox>("DeadlineMode").SelectedIndex == 2 && Find<System.Windows.Controls.TextBox>("DeadlineMinutes").Text == "30" &&
                      Find<Nagster.Controls.SoundPicker>("SoundChoice").Sound == "builtin:chime" &&
                      Find<System.Windows.Controls.TextBox>("ResponseOverride").Text == "45" && Find<System.Windows.Controls.TextBox>("AutoSnoozeOverride").Text == "2h");
        editSteps.Add((string)((System.Windows.Controls.ComboBoxItem)Find<System.Windows.Controls.ComboBox>("DevicesBox").SelectedItem).Tag == "Phone" &&
                      Find<System.Windows.Controls.CheckBox>("ImportantBox").IsChecked == true);
        System.Windows.Window[] dialogs = [new MainWindow(), new SettingsWindow(), new AlarmCenterWindow(), new QuickReminderWindow(), new NewAlarmWindow(),
            new RescheduleWindow(new AlarmOccurrence { AlertAt = DateTime.Now.AddHours(1) }), new CalendarsWindow()];
        placementSteps.Add(dialogs.All(w => w.WindowStartupLocation == System.Windows.WindowStartupLocation.CenterScreen));
        var endedAt = DateTime.Today.AddHours(15).AddMinutes(45);
        var pauseEnded = new PauseEndedPopup(endedAt, 2);
        pauseWording = ((System.Windows.Controls.TextBlock)pauseEnded.FindName("DetailText")).Text;
        var editor = new NewAlarmWindow();
        var startDate = (System.Windows.Controls.DatePicker)editor.FindName("DueDate");
        var startTime = (Nagster.Controls.TimePicker)editor.FindName("DueTime");
        var endDate = (System.Windows.Controls.DatePicker)editor.FindName("EventEndDate");
        var endTime = (Nagster.Controls.TimePicker)editor.FindName("EventEndTime");
        var duration = (System.Windows.Controls.ComboBox)editor.FindName("EventDuration");
        DateTime End() => endDate.SelectedDate!.Value.Date + endTime.SelectedTime!.Value;
        string Duration() => (string)((System.Windows.Controls.ComboBoxItem)duration.SelectedItem).Content;
        var day = DateTime.Today.AddDays(3);
        startDate.SelectedDate = day;
        startTime.SelectedTime = new TimeSpan(9, 0, 0);
        durationSteps.Add(End() == day.AddHours(10) && Duration() == "1 hour");                     // default 1 hour follows start
        duration.SelectedIndex = duration.Items.Cast<System.Windows.Controls.ComboBoxItem>().ToList().FindIndex(i => (string)i.Content == "2 hours");
        durationSteps.Add(End() == day.AddHours(11));                                              // duration change moves end
        startTime.SelectedTime = new TimeSpan(9, 30, 0);
        durationSteps.Add(End() == day.AddHours(11.5));                                            // start change keeps duration
        endTime.SelectedTime = new TimeSpan(10, 20, 0);
        durationSteps.Add(Duration() == "45 minutes" && End() == day.AddHours(10).AddMinutes(20)); // 50 min -> nearest 45, end kept
        startDate.SelectedDate = day.AddDays(1);
        durationSteps.Add(End() == day.AddDays(1).AddHours(9.5).AddMinutes(45));                   // new start date + 45 min
        endDate.SelectedDate = day.AddDays(3);
        durationSteps.Add(Duration() == "2 days");                                                  // ~2 days later -> 2 days
        _ = new RescheduleWindow(new AlarmOccurrence { AlertAt = DateTime.Now.AddHours(1) });
        _ = new AlarmPopup(new AlarmOccurrence { ScheduledFor = DateTime.Now },
            new AlarmDefinition { Title = "UI check" }, new AppSettings());
    }
    catch (Exception error) { uiError = error; }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();
if (File.Exists(uiPath)) File.Delete(uiPath);
Check(uiError is null, "WPF alarm/editor windows construct");
Check(durationSteps.Count == 6 && durationSteps.All(ok => ok), $"event duration links start and end ({string.Join(", ", durationSteps.Select((ok, i) => $"step {i + 1} {(ok ? "ok" : "FAILED")}"))})");
Check(editSteps.Count == 7 && editSteps.All(ok => ok), $"edit window loads every field of an existing alarm ({string.Join(", ", editSteps.Select((ok, i) => $"step {i + 1} {(ok ? "ok" : "FAILED")}"))})");
Check(placementSteps.Count == 1 && placementSteps.All(ok => ok), "every window opens centred on the screen");
Check(pauseWording.StartsWith("Your pause ended at ") && pauseWording.EndsWith("2 alarms are waiting to go off."), $"the pause-ended popup says when it ended and what is waiting ({pauseWording})");
