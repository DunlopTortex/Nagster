using System.Globalization;
using System.Windows.Threading;

namespace DGNaggingAlarms.Calendars;

/// <summary>
/// Periodically reads every enabled calendar and turns new events into alarms (see <see cref="ImportPlanner"/>).
/// Fetching happens off the UI thread; planning and creating alarms happen on it, because the engine isn't
/// thread-safe. Each calendar's outcome is recorded in its LastResult for Settings.
/// </summary>
public sealed class CalendarImporter
{
    private readonly AlarmEngine _engine;
    private readonly DispatcherTimer _timer;
    private DateTime _nextRun = DateTime.Now.AddSeconds(20); // Let the app finish starting first.
    private bool _running;

    public CalendarImporter(AlarmEngine engine)
    {
        _engine = engine;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => { if (DateTime.Now >= _nextRun) await SyncAllAsync(); };
        _timer.Start();
    }

    public static ICalendarProvider ProviderFor(CalendarSourceKind kind) => kind switch
    {
        CalendarSourceKind.Google => new GoogleCalendarProvider(),
        CalendarSourceKind.Microsoft365 => new MicrosoftCalendarProvider(),
        CalendarSourceKind.OutlookDesktop => new OutlookDesktopProvider(),
        _ => new IcsCalendarProvider()
    };

    /// <summary>Syncs all enabled calendars now (e.g. after Settings change) and schedules the next run.</summary>
    public async Task SyncAllAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            var settings = _engine.State.Settings.Calendars;
            foreach (var source in settings.Sources.Where(s => s.Enabled).ToList())
                source.LastResult = await SyncAsync(source, settings);
            if (settings.Sources.Count > 0) _engine.SaveSettings();
            _nextRun = DateTime.Now.AddMinutes(Math.Clamp(settings.IntervalMinutes, 5, 1440));
        }
        finally { _running = false; }
    }

    private async Task<string> SyncAsync(CalendarSource source, CalendarSyncSettings settings)
    {
        var now = DateTime.Now;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var events = await Task.Run(() => ProviderFor(source.Kind)
                .FetchAsync(source, settings, now, now.AddDays(Math.Clamp(settings.LookaheadDays, 1, 60)), timeout.Token));
            var plans = ImportPlanner.Plan(events, source, settings, _engine.ImportedEventKeys(), DateTime.Now);
            var created = _engine.Import(plans, DateTime.Now);
            return $"Synced {Stamp()}: {Describe(created, "new alarm")} from {Describe(events.Count, "event")}";
        }
        catch (CalendarException ex) { return $"Failed {Stamp()}: {ex.Message}"; }
        catch (OperationCanceledException) { return $"Failed {Stamp()}: the calendar took too long to respond."; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Calendar sync failed for {source.Name}: {ex}");
            return $"Failed {Stamp()}: {ex.Message}";
        }
    }

    private static string Stamp() => DateTime.Now.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public void Stop() => _timer.Stop();
}
