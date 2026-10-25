using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nagster;
using Nagster.Cloud;
using Nagster.Web;

/// <summary>
/// Checks for cloud sync without a network: the encryption against shared/cloud/crypto-vectors.json (the iPhone
/// app's tests read the same file), and the PC's side of the rules, as master of the shared copy.
/// </summary>
internal static class CloudChecks
{
    private sealed class EngineActions(AlarmEngine engine) : IAlarmActions
    {
        public AlarmEngine Engine => engine;
        public void Complete(AlarmOccurrence occurrence) => engine.Complete(occurrence);
        public void Dismiss(AlarmOccurrence occurrence) => engine.Dismiss(occurrence);
        public bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message, bool replay = false) =>
            engine.TrySnooze(occurrence, next, automatic, out message, replay);
        public bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message, bool replay = false) =>
            engine.TryReschedule(occurrence, next, out message, replay);
        public void DeleteDefinition(Guid definitionId) => engine.Delete(definitionId);
    }

    public static void Run(Action<bool, string> check)
    {
        CheckEncryption(check);
        var path = Path.Combine(Path.GetTempPath(), "Nagster-cloud-" + Guid.NewGuid() + ".db");
        try { CheckMaster(check, new AlarmEngine(new AlarmStore(path))); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void CheckEncryption(Action<bool, string> check)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "shared", "cloud"))) folder = folder.Parent;
        var v = JsonNode.Parse(File.ReadAllText(Path.Combine(folder!.FullName, "shared", "cloud", "crypto-vectors.json")))!;
        string S(string key) => v[key]!.GetValue<string>();
        byte[] B(string key) => Convert.FromBase64String(S(key));
        var kek = CloudCrypto.DeriveKey(S("passphrase"), B("salt"), v["iterations"]!.GetValue<int>());
        check(kek.SequenceEqual(B("kek")) && CloudCrypto.WrapKey(B("dataKey"), kek, S("userId"), B("wrapNonce")) == S("wrappedKey") &&
              CloudCrypto.UnwrapKey(S("wrappedKey"), kek, S("userId")).SequenceEqual(B("dataKey")), "cloud: the vault matches the shared vectors");
        var context = CloudCrypto.RecordContext(S("userId"), Guid.Parse(S("recordId")));
        check(CloudCrypto.SealJson(S("plaintext"), B("dataKey"), context, B("entryNonce")) == S("payload") &&
              CloudCrypto.OpenJson(S("payload"), B("dataKey"), context) == S("plaintext"), "cloud: a record seals as the shared vectors say");

        static bool Refused(Action action)
        {
            try { action(); return false; }
            catch (CloudCryptoException) { return true; }
        }
        var altered = B("payload");
        altered[20] ^= 1;
        check(Refused(() => CloudCrypto.UnwrapKey(S("wrappedKey"), CloudCrypto.DeriveKey("not my passphrase", B("salt"), 1000), S("userId"))) &&
              Refused(() => CloudCrypto.OpenJson(S("payload"), B("dataKey"), CloudCrypto.RecordContext(S("userId"), Guid.NewGuid()))) &&
              Refused(() => CloudCrypto.OpenJson(Convert.ToBase64String(altered), B("dataKey"), context)),
            "cloud: a wrong passphrase, moved ciphertext and altered ciphertext are all refused");
    }

    private static void CheckMaster(Action<bool, string> check, AlarmEngine engine)
    {
        var actions = new EngineActions(engine);
        var state = new CloudState { UserId = "u" };
        var tomorrow = DateTime.Today.AddDays(1);
        engine.Add(new AlarmDefinition { Title = "Bins", FirstDue = tomorrow.AddHours(9), Repeat = new RepeatRule { Kind = RepeatKind.Daily } });
        engine.Ready(DateTime.Now);

        // Publish everything, as a first round would, numbering records 1, 2, 3…
        var first = CloudSync.CollectOutgoing(engine, state, DateTime.Now);
        long seq = 0;
        foreach (var o in first) state.Known[o.Id] = new CloudKnown { Kind = o.Kind, Seq = ++seq, LocalRevision = o.LocalRevision, Digest = o.Digest };
        check(first.Count(o => o.Kind == "alarm") == 1 && first.Count(o => o.Kind == "settings") == 1 && first.Count(o => o.Kind == "occurrence") > 1 &&
              first.All(o => !o.Json!.Contains("\"revision\":1")), "cloud: the first round publishes the alarm, its occurrences and the settings");
        check(CloudSync.CollectOutgoing(engine, state, DateTime.Now).Count == 0, "cloud: nothing changed means nothing to publish");

        // An action against the latest copy keeps its place; one against an older copy counts as made against nothing.
        var occurrence = engine.State.Occurrences.OrderBy(o => o.ScheduledFor).First();
        var known = state.Known[occurrence.Id];
        var current = CloudSync.Translate(state, [new SyncOp(Guid.NewGuid(), SyncOps.Complete, known.Seq, occurrence.Id)]).Single();
        var stale = CloudSync.Translate(state, [new SyncOp(Guid.NewGuid(), SyncOps.Complete, known.Seq - 1, occurrence.Id)]).Single();
        check(current.BaseRevision == occurrence.Revision && stale.BaseRevision == 0, "cloud: base revisions translate from cloud seqs to the PC's");

        // Answered on the PC since the phone last saw it: the phone's snooze is turned down, as over the network.
        actions.Dismiss(occurrence);
        var snooze = new SyncOp(Guid.NewGuid(), SyncOps.Snooze, known.Seq, occurrence.Id, occurrence.DefinitionId,
            SyncTime.Format(occurrence.ScheduledFor), SyncTime.Format(DateTime.Now.AddHours(2)));
        var refused = SyncApi.Apply(actions, CloudSync.Translate(state, [snooze]), DateTime.Now).Single();
        check(!refused.Applied && engine.State.Occurrences.First(o => o.Id == occurrence.Id).Status == OccurrenceStatus.Dismissed,
            "cloud: a phone action that lost a race is turned down");
        check(CloudSync.CollectOutgoing(engine, state, DateTime.Now).Single().Id == occurrence.Id, "cloud: only the changed occurrence goes up");

        // What an iPhone applied while the PC was away comes in: a snooze, and an alarm made on the phone.
        var next = engine.State.Occurrences.Where(o => o.Status == OccurrenceStatus.Pending).OrderBy(o => o.ScheduledFor).First();
        var snoozedTo = next.AlertAt.AddHours(2);
        var wire = SyncApi.ToWire(next) with { Status = "Snoozed", AlertAt = SyncTime.Format(snoozedTo), SnoozeCount = 1, LastAction = "Snoozed on the phone" };
        var phoneAlarm = new SyncDefinition(Guid.NewGuid(), "Call mum", "", false, null, 0, SyncTime.Format(tomorrow.AddHours(18)),
            new SyncRepeat("Once", 1, 0, 0, 0, 0, true, null, null), null, null, "All", false, true, null, 0);
        CloudSync.Adopt(actions, state,
        [
            (new CloudRow(next.Id, "occurrence", 1000, false, "x"), JsonSerializer.Serialize(new CloudOccurrence(wire, false), CloudCrypto.Json)),
            (new CloudRow(phoneAlarm.Id, "alarm", 1001, false, "x"), JsonSerializer.Serialize(phoneAlarm, CloudCrypto.Json))
        ]);
        var adopted = engine.State.Occurrences.First(o => o.Id == next.Id);
        check(adopted.Status == OccurrenceStatus.Snoozed && adopted.AlertAt == snoozedTo && adopted.SnoozeCount == 1 &&
              engine.State.Definitions.Any(d => d.Id == phoneAlarm.Id), "cloud: the PC takes in what an iPhone applied while it was away");

        // A finished occurrence here never reopens, whatever comes in.
        var reopen = SyncApi.ToWire(occurrence) with { Status = "Pending" };
        CloudSync.Adopt(actions, state,
            [(new CloudRow(occurrence.Id, "occurrence", 1002, false, "x"), JsonSerializer.Serialize(new CloudOccurrence(reopen, false), CloudCrypto.Json))]);
        check(engine.State.Occurrences.First(o => o.Id == occurrence.Id).Status == OccurrenceStatus.Dismissed, "cloud: a finished occurrence never reopens");

        // A deleted alarm in the cloud is deleted here; its records go out as deletions.
        CloudSync.Adopt(actions, state, [(new CloudRow(phoneAlarm.Id, "alarm", 1003, true, null), null)]);
        check(engine.State.Definitions.All(d => d.Id != phoneAlarm.Id), "cloud: an alarm deleted elsewhere is deleted here");
    }
}
