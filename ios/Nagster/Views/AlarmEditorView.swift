import NagsterCore
import SwiftUI

/// Creates or edits an alarm or event, with the same choices as the PC's editor.
struct AlarmEditorView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var close
    private let existing: AlarmDefinition?
    /// The start shown when editing (the next occurrence for a repeating alarm), to tell whether it was changed.
    private let initialStart: Date

    @State private var title: String
    @State private var notes: String
    @State private var isEvent: Bool
    @State private var start: Date
    @State private var end: Date
    @State private var reminderMinutes: Int
    @State private var kind: RepeatKind
    @State private var interval: Int
    @State private var weekdays: Set<Int>
    @State private var monthlyByWeekday: Bool
    @State private var nth: Int
    @State private var nthWeekday: Int
    @State private var ending: Ending
    @State private var until: Date
    @State private var count: Int
    @State private var deadline: Deadline
    @State private var deadlineMinutes: Int
    @State private var deadlineAt: Date
    @State private var devices: AlarmDevices
    @State private var important: Bool
    @State private var nagChoice: NagChoice
    @State private var problem: String?

    /// How this alarm nags: as Settings says, not at all (this iPhone only), or at its own length (synced with the PC).
    enum NagChoice: Hashable {
        case standard, off, every(SnoozeDuration)
    }

    enum Ending: String, CaseIterable, Identifiable {
        case never = "Never", onDate = "On a date", afterCount = "After a number of times"
        var id: Self { self }
    }

    enum Deadline: String, CaseIterable, Identifiable {
        case none = "No deadline", afterEach = "Minutes after each alert", fixed = "At a fixed time"
        var id: Self { self }
    }

    private static let weekdayNames = Calendar.current.shortWeekdaySymbols // Sunday first, matching the PC.
    private static let nthNames = [1: "First", 2: "Second", 3: "Third", 4: "Fourth", -1: "Last"]

    /// - Parameter nagOff: Whether this alarm's reminders are turned off on this phone.
    init(definition: AlarmDefinition?, nextRun: WallTime? = nil, nagOff: Bool = false) {
        existing = definition
        let rule = definition?.repeatRule ?? RepeatRule()
        let firstDue = (nextRun ?? definition?.firstDue)?.date() ?? Self.roundedUp(Date().addingTimeInterval(3600))
        initialStart = firstDue
        _title = State(initialValue: definition?.title ?? "")
        _notes = State(initialValue: definition?.description ?? "")
        _isEvent = State(initialValue: definition?.isEvent ?? false)
        _start = State(initialValue: firstDue)
        let length = definition.flatMap { d in d.eventEnd.map { $0 - d.firstDue } } ?? 3600
        _end = State(initialValue: firstDue.addingTimeInterval(length))
        _reminderMinutes = State(initialValue: definition?.reminderMinutes ?? 15)
        _kind = State(initialValue: rule.kind)
        _interval = State(initialValue: rule.interval)
        _weekdays = State(initialValue: Set((0..<7).filter { rule.weekdays & (1 << $0) != 0 }))
        _monthlyByWeekday = State(initialValue: rule.nth != 0)
        _nth = State(initialValue: rule.nth == 0 ? 1 : rule.nth)
        _nthWeekday = State(initialValue: rule.nthWeekday)
        _ending = State(initialValue: rule.count != nil ? .afterCount : rule.until != nil ? .onDate : .never)
        _until = State(initialValue: rule.until?.date() ?? firstDue.addingTimeInterval(30 * 86_400))
        _count = State(initialValue: rule.count ?? 10)
        _deadline = State(initialValue: definition?.deadlineAfterMinutes != nil ? .afterEach
                          : definition?.absoluteDeadline != nil ? .fixed : .none)
        _deadlineMinutes = State(initialValue: definition?.deadlineAfterMinutes ?? 60)
        _deadlineAt = State(initialValue: definition?.absoluteDeadline?.date() ?? firstDue.addingTimeInterval(3600))
        _devices = State(initialValue: definition?.devices ?? .all)
        _important = State(initialValue: definition?.important ?? false)
        _nagChoice = State(initialValue: nagOff ? .off : definition?.nagAfter.map(NagChoice.every) ?? .standard)
    }

    var body: some View {
        NavigationStack {
            Form {
                // The dial's hands follow the chosen time.
                Section {
                    HStack(spacing: 16) {
                        ClockDial(time: WallTime(start), ringColor: Theme.violet)
                            .frame(width: 68, height: 68)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(start.formatted(date: .omitted, time: .shortened))
                                .font(.system(size: 34, weight: .bold, design: .rounded).monospacedDigit())
                                .foregroundStyle(Theme.ink)
                                .lineLimit(1)
                                .minimumScaleFactor(0.7)
                            Text(start.formatted(.dateTime.weekday(.wide).day().month(.wide)))
                                .foregroundStyle(Theme.secondaryText)
                        }
                    }
                    .listRowBackground(Color.clear)
                    .animation(.snappy, value: start)
                }

                Section {
                    Picker("Type", selection: $isEvent) {
                        Text("Alarm").tag(false)
                        Text("Event").tag(true)
                    }
                    .pickerStyle(.segmented)
                    TextField("What needs doing?", text: $title)
                    TextField("Notes (optional)", text: $notes, axis: .vertical)
                        .lineLimit(2...5)
                }

                Section(isEvent ? "When" : "First alert") {
                    DatePicker(isEvent ? "Starts" : "Alert at", selection: $start)
                    if isEvent {
                        DatePicker("Ends", selection: $end, in: start...)
                        Stepper("Remind me \(reminderMinutes) min before", value: $reminderMinutes, in: 0...10_080, step: 5)
                    }
                }

                repeatSection

                Section("Deadline") {
                    Picker("Must complete", selection: $deadline) {
                        ForEach(Deadline.allCases) { Text($0.rawValue).tag($0) }
                    }
                    switch deadline {
                    case .none: EmptyView()
                    case .afterEach: Stepper("Within \(deadlineMinutes) min of each alert", value: $deadlineMinutes, in: 5...10_080, step: 5)
                    case .fixed: DatePicker("Complete by", selection: $deadlineAt, in: start...)
                    }
                }

                Section {
                    Picker("Nag again after", selection: $nagChoice) {
                        Text(model.naggingEnabled ? "Default (\(model.nagInterval.label))" : "Default (off)").tag(NagChoice.standard)
                        Text("Off").tag(NagChoice.off)
                        ForEach(nagChoices, id: \.self) { Text($0.label).tag(NagChoice.every($0)) }
                    }
                } header: {
                    Text("Reminders")
                } footer: {
                    Text(model.naggingEnabled
                         ? "How long this alarm waits before reminding you again if you don't answer. Off stops reminders "
                           + "for it on this iPhone; the PC still nags."
                         : "Nagging is turned off in Settings, so every alarm alerts once on this iPhone.")
                }

                Section {
                    Picker("Ring on", selection: $devices) {
                        ForEach(AlarmDevices.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    Toggle(isOn: $important) {
                        VStack(alignment: .leading) {
                            Text("Important")
                            Text("Rings full screen like the Clock app, even in silent mode.")
                                .font(.footnote).foregroundStyle(.secondary)
                        }
                    }
                    .disabled(devices == .desktop)
                } header: {
                    Text("Devices")
                } footer: {
                    Text("Every device lists the alarm; only these ones ring.")
                }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.canvas)
            .navigationTitle(existing == nil ? "New alarm" : "Edit alarm")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { close() } }
                ToolbarItem(placement: .confirmationAction) { Button(existing == nil ? "Add" : "Save", action: save) }
            }
            .onChange(of: start) { oldStart, newStart in
                // Keep an event's length when its start moves.
                end = end.addingTimeInterval(newStart.timeIntervalSince(oldStart))
            }
            .onChange(of: important) {
                guard important, !model.importantAlarmsAllowed else { return }
                Task {
                    if !(await model.requestImportantAlarms()) {
                        problem = "Important alarms aren't allowed, so this one will ring as a notification. Allow them in Settings → Nagster."
                    }
                }
            }
            .alert("Check the alarm", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(problem ?? "")
            }
        }
    }

    private var repeatSection: some View {
        Section("Repeat") {
            Picker("Repeats", selection: $kind) {
                Text("Never").tag(RepeatKind.once)
                Text("Every few minutes").tag(RepeatKind.minutes)
                Text("Every few hours").tag(RepeatKind.hours)
                Text("Daily").tag(RepeatKind.daily)
                Text("Weekly").tag(RepeatKind.weekly)
                Text("Monthly").tag(RepeatKind.monthly)
                Text("Yearly").tag(RepeatKind.yearly)
            }
            if kind != .once {
                Stepper("Every \(interval) \(unit)", value: $interval, in: 1...999)
            }
            if kind == .weekly {
                HStack {
                    ForEach(0..<7, id: \.self) { day in
                        Toggle(Self.weekdayNames[day], isOn: Binding(
                            get: { weekdays.contains(day) },
                            set: { if $0 { weekdays.insert(day) } else { weekdays.remove(day) } }))
                            .toggleStyle(.button)
                            .buttonStyle(.bordered)
                            .font(.caption)
                    }
                }
                Text("Leave all unselected to repeat on the first alert's weekday.").font(.footnote).foregroundStyle(.secondary)
            }
            if kind == .monthly || kind == .yearly {
                Toggle("On a weekday of the month", isOn: $monthlyByWeekday)
                if monthlyByWeekday {
                    Picker("Which", selection: $nth) {
                        ForEach([1, 2, 3, 4, -1], id: \.self) { Text(Self.nthNames[$0] ?? "").tag($0) }
                    }
                    Picker("Day", selection: $nthWeekday) {
                        ForEach(0..<7, id: \.self) { Text(Calendar.current.weekdaySymbols[$0]).tag($0) }
                    }
                }
            }
            if kind != .once {
                Picker("Stop repeating", selection: $ending) {
                    ForEach(Ending.allCases) { Text($0.rawValue).tag($0) }
                }
                switch ending {
                case .never: EmptyView()
                case .onDate: DatePicker("After", selection: $until, in: start..., displayedComponents: .date)
                case .afterCount: Stepper("\(count) times in all", value: $count, in: 1...999)
                }
            }
        }
    }

    /// The usual lengths, plus this alarm's own if it was set to something else on the PC.
    private var nagChoices: [SnoozeDuration] {
        var choices = AppModel.nagIntervalChoices
        if case .every(let length) = nagChoice, !choices.contains(length) { choices.append(length) }
        return choices
    }

    private var unit: String {
        let units: [RepeatKind: (String, String)] = [.minutes: ("minute", "minutes"), .hours: ("hour", "hours"), .daily: ("day", "days"),
                                                     .weekly: ("week", "weeks"), .monthly: ("month", "months"), .yearly: ("year", "years")]
        let (one, many) = units[kind] ?? ("", "")
        return interval == 1 ? one : many
    }

    private func save() {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        if let reason = validationProblem(title: trimmed) {
            problem = reason
            return
        }

        var rule = RepeatRule(kind: kind, interval: kind == .once ? 1 : interval)
        if kind == .weekly { rule.weekdays = weekdays.reduce(0) { $0 | (1 << $1) } }
        if (kind == .monthly || kind == .yearly) && monthlyByWeekday {
            rule.nth = nth
            rule.nthWeekday = nthWeekday
        }
        if kind != .once {
            switch ending {
            case .never: break
            case .onDate: rule.until = WallTime(Calendar.current.date(bySettingHour: 23, minute: 59, second: 59, of: until) ?? until)
            case .afterCount: rule.count = count
            }
        }

        // An unchanged start keeps the alarm's original first alert, so the PC sees the schedule as unchanged.
        var firstDue = WallTime(start)
        if let existing, start == initialStart { firstDue = existing.firstDue }
        var definition = existing ?? AlarmDefinition(title: trimmed, firstDue: firstDue)
        definition.title = trimmed
        definition.description = notes.trimmingCharacters(in: .whitespacesAndNewlines)
        definition.isEvent = isEvent
        definition.firstDue = firstDue
        definition.eventEnd = isEvent ? firstDue.adding(seconds: end.timeIntervalSince(start)) : nil
        definition.reminderMinutes = isEvent ? reminderMinutes : 0
        definition.repeatRule = rule
        definition.deadlineAfterMinutes = deadline == .afterEach ? deadlineMinutes : nil
        definition.absoluteDeadline = deadline == .fixed ? WallTime(deadlineAt) : nil
        definition.devices = devices
        definition.important = important && devices != .desktop
        switch nagChoice {
        case .standard: definition.autoSnoozeOverride = nil
        case .every(let length): definition.autoSnoozeOverride = length.description
        case .off: break // Phone-only; the length shared with the PC stays as it was.
        }
        model.save(definition, nags: nagChoice != .off)
        close()
    }

    private func validationProblem(title: String) -> String? {
        if !(1...160).contains(title.count) { return "Enter a title (up to 160 characters)." }
        if notes.count > 1000 { return "Keep the notes under 1000 characters." }
        if isEvent && end <= start { return "The event must end after it starts." }
        if deadline == .fixed && deadlineAt <= start { return "The deadline must be after the first alert." }
        return nil
    }

    /// The next whole five minutes, for a new alarm's default time.
    private static func roundedUp(_ date: Date) -> Date {
        let calendar = Calendar.current
        let minuteStart = calendar.dateInterval(of: .minute, for: date)?.start ?? date
        let minute = calendar.component(.minute, from: minuteStart)
        return calendar.date(byAdding: .minute, value: (5 - minute % 5) % 5, to: minuteStart) ?? date
    }
}
