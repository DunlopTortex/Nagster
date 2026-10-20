import NagsterCore
import SwiftUI

/// The Alarms tab: a live clock dial for what's ringing or next, then the coming fortnight as a timeline.
struct AgendaView: View {
    @Environment(AppModel.self) private var model
    @State private var selected: AlarmOccurrence?
    @State private var creating = false
    @State private var showingSettings = false
    @State private var showingAnswered = false

    var body: some View {
        NavigationStack {
            TimelineView(.periodic(from: .now, by: 30)) { context in
                content(now: WallTime(context.date))
            }
            .background(Theme.canvas)
            // The wordmark header replaces the system title bar on this tab.
            .toolbar(.hidden, for: .navigationBar)
            .refreshable { await model.syncNow() }
            .safeAreaInset(edge: .bottom) { SyncStatusPill() }
            .sheet(item: $selected) { occurrence in OccurrenceSheet(occurrence: occurrence) }
            .sheet(isPresented: $creating) { AlarmEditorView(definition: nil) }
            .sheet(isPresented: $showingSettings) { SettingsView() }
        }
        .onChange(of: model.focused) {
            if let focused = model.focused {
                selected = focused
                model.focused = nil
            }
        }
    }

    private func content(now: WallTime) -> some View {
        let ringing = model.ringing
        let upcoming = model.upcoming()
        let featured = ringing.first ?? upcoming.first
        let comingUp = upcoming.filter { $0.id != featured?.id }
        return ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                header

                NowPanel(occurrence: featured, isRinging: !ringing.isEmpty, alsoRinging: max(ringing.count - 1, 0), now: now,
                         open: { selected = $0 }, add: { creating = true })

                if model.isPaused {
                    Badge(symbol: "pause.circle.fill", text: "Alerts on this iPhone are paused", tint: Theme.violet)
                }

                if ringing.count > 1 {
                    day("Also going off", ringing.dropFirst())
                }
                ForEach(groupedByDay(comingUp), id: \.title) { group in
                    day(group.title, group.occurrences)
                }

                answered
            }
            .padding(.horizontal, 20)
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
    }

    /// The app's name as a wordmark beside a small copy of its icon, with Settings and New alarm.
    private var header: some View {
        HStack(spacing: 12) {
            AppMark()
                .frame(width: 38, height: 38)
            Text("Nagster")
                .font(.system(size: 32, weight: .heavy, design: .rounded))
                .tracking(-0.6)
                .foregroundStyle(Theme.ink)
                .accessibilityAddTraits(.isHeader)
            Spacer()
            Button { showingSettings = true } label: {
                Image(systemName: "gearshape.fill")
                    .font(.system(size: 17, weight: .semibold))
                    .frame(width: 42, height: 42)
                    .foregroundStyle(Theme.ink)
                    .background(Theme.violet.opacity(0.14), in: Circle())
            }
            .accessibilityLabel("Settings")
            Button { creating = true } label: {
                Image(systemName: "plus")
                    .font(.system(size: 19, weight: .bold))
                    .frame(width: 42, height: 42)
                    .foregroundStyle(.white)
                    .background(Theme.brand, in: Circle())
                    .shadow(color: Theme.deep.opacity(0.3), radius: 6, y: 3)
            }
            .accessibilityLabel("New alarm")
        }
        .padding(.top, 4)
    }

    private func day(_ title: String, _ occurrences: some Collection<AlarmOccurrence>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.rounded(.title3, weight: .bold))
                .foregroundStyle(Theme.ink)
                .padding(.bottom, 8)
            ForEach(Array(occurrences.enumerated()), id: \.element.id) { index, occurrence in
                Button { selected = occurrence } label: {
                    TimelineRow(occurrence: occurrence, definition: model.definition(of: occurrence),
                                isLast: index == occurrences.count - 1)
                }
                .buttonStyle(.plain)
                .contextMenu { quickActions(for: occurrence) }
            }
        }
    }

    @ViewBuilder
    private var answered: some View {
        let recent = model.recent
        if !recent.isEmpty {
            DisclosureGroup(isExpanded: $showingAnswered) {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(Array(recent.enumerated()), id: \.element.id) { index, occurrence in
                        Button { selected = occurrence } label: {
                            TimelineRow(occurrence: occurrence, definition: model.definition(of: occurrence),
                                        isLast: index == recent.count - 1)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.top, 12)
            } label: {
                Text("Answered this week (\(recent.count))")
                    .font(.rounded(.headline))
                    .foregroundStyle(Theme.secondaryText)
            }
            .tint(Theme.secondaryText)
        }
    }

    @ViewBuilder
    private func quickActions(for occurrence: AlarmOccurrence) -> some View {
        Button("Complete", systemImage: "checkmark.circle") { model.complete(occurrence) }
        if let first = model.state.settings.snoozeDurations.first {
            Button("Snooze \(first.label)", systemImage: "zzz") { model.snooze(occurrence, for: first) }
        }
        Button("Dismiss", systemImage: "xmark.circle", role: .destructive) { model.dismiss(occurrence) }
    }

    /// Occurrences under "Today", "Tomorrow" or the date, in time order.
    private func groupedByDay(_ occurrences: [AlarmOccurrence]) -> [(title: String, occurrences: [AlarmOccurrence])] {
        var groups: [(title: String, occurrences: [AlarmOccurrence])] = []
        for occurrence in occurrences {
            let title = Formatting.longDay(occurrence.alertAt)
            if groups.last?.title == title {
                groups[groups.count - 1].occurrences.append(occurrence)
            } else {
                groups.append((title, [occurrence]))
            }
        }
        return groups
    }
}

/// The hero: the alarm ringing now, or the next one, on the app's gradient with a live clock dial.
private struct NowPanel: View {
    @Environment(AppModel.self) private var model
    var occurrence: AlarmOccurrence?
    var isRinging: Bool
    var alsoRinging: Int
    var now: WallTime
    var open: (AlarmOccurrence) -> Void
    var add: () -> Void
    @State private var completed = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .center, spacing: 18) {
                ClockDial(time: occurrence?.alertAt ?? now, progress: progress, urgent: isRinging)
                    .frame(width: 116, height: 116)
                VStack(alignment: .leading, spacing: 4) {
                    Text(heading)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(.white.opacity(0.8))
                    Text(title)
                        .font(.rounded(.title2, weight: .bold))
                        .foregroundStyle(.white)
                        .lineLimit(2)
                        .minimumScaleFactor(0.8)
                    if let occurrence {
                        Text(Formatting.time(occurrence.alertAt))
                            .font(.system(size: 32, weight: .bold, design: .rounded).monospacedDigit())
                            .foregroundStyle(.white)
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                    }
                    Text(detail)
                        .font(.footnote.weight(.medium))
                        .foregroundStyle(.white.opacity(0.85))
                }
                Spacer(minLength: 0)
            }

            if let occurrence, isRinging {
                HStack(spacing: 10) {
                    Button {
                        model.complete(occurrence)
                        completed += 1
                    } label: {
                        Label("Complete", systemImage: "checkmark")
                            .font(.rounded(.headline))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 12)
                            .foregroundStyle(Theme.dialInk)
                            .background(.white, in: Capsule())
                    }
                    if let first = model.state.settings.snoozeDurations.first {
                        Button { model.snooze(occurrence, for: first) } label: {
                            Label(first.label, systemImage: "zzz")
                                .font(.rounded(.headline))
                                .padding(.vertical, 12)
                                .padding(.horizontal, 16)
                                .foregroundStyle(.white)
                                .background(.white.opacity(0.22), in: Capsule())
                        }
                    }
                    Button { open(occurrence) } label: {
                        Image(systemName: "ellipsis")
                            .font(.headline)
                            .frame(width: 46, height: 46)
                            .foregroundStyle(.white)
                            .background(.white.opacity(0.22), in: Circle())
                    }
                    .accessibilityLabel("More choices")
                }
                .sensoryFeedback(.success, trigger: completed)
            } else if occurrence == nil {
                Button(action: add) {
                    Label("Add an alarm", systemImage: "plus")
                        .font(.rounded(.headline))
                        .padding(.vertical, 12)
                        .padding(.horizontal, 20)
                        .foregroundStyle(Theme.dialInk)
                        .background(.white, in: Capsule())
                }
            }
        }
        .padding(20)
        .background(isRinging ? Theme.nagging : Theme.brand, in: RoundedRectangle(cornerRadius: 32, style: .continuous))
        .shadow(color: (isRinging ? Theme.nag : Theme.deep).opacity(0.35), radius: 18, y: 10)
        .contentShape(RoundedRectangle(cornerRadius: 32, style: .continuous))
        .onTapGesture { if let occurrence { open(occurrence) } }
        .accessibilityElement(children: .contain)
    }

    private var definition: AlarmDefinition? { occurrence.flatMap { model.definition(of: $0) } }

    private var heading: String {
        guard occurrence != nil else { return "All quiet" }
        return isRinging ? "Nagging you" : "Next up"
    }

    private var title: String {
        guard occurrence != nil else { return "Nothing due" }
        return definition?.title ?? "Alarm"
    }

    private var detail: String {
        guard let occurrence else { return "Nothing in the next two weeks." }
        if isRinging {
            var parts = [occurrence.snoozeCount > 0 ? "Snoozed \(occurrence.snoozeCount)×" : "Due \(Formatting.time(occurrence.scheduledFor))"]
            if let deadline = occurrence.deadline { parts.append("complete by \(Formatting.time(deadline))") }
            if alsoRinging > 0 { parts.append("\(alsoRinging) more going off") }
            return parts.joined(separator: ", ")
        }
        let when = occurrence.alertAt.date().formatted(.relative(presentation: .named))
        return Formatting.day(occurrence.alertAt) == "Today" ? when.prefix(1).uppercased() + when.dropFirst()
            : "\(Formatting.day(occurrence.alertAt)), \(when)"
    }

    /// Fills over the last hour before the alarm; full while it rings.
    private var progress: Double {
        guard let occurrence else { return 0 }
        if isRinging { return 1 }
        let minutesLeft = (occurrence.alertAt - now) / 60
        return max(0.03, 1 - minutesLeft / 60)
    }
}

/// One occurrence on the timeline: its time on the left, a dot on the rail, and what it is.
struct TimelineRow: View {
    var occurrence: AlarmOccurrence
    var definition: AlarmDefinition?
    var isLast = false

    private var finished: Bool { occurrence.status.isFinished }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(Formatting.time(occurrence.alertAt))
                .font(.rounded(.title3).monospacedDigit())
                .foregroundStyle(finished ? Theme.secondaryText : Theme.ink)
                .frame(width: 72, alignment: .trailing)

            VStack(spacing: 0) {
                marker
                    .frame(width: 14, height: 14)
                    .padding(.top, 6)
                if !isLast {
                    Rectangle().fill(Theme.rail).frame(width: 2).frame(maxHeight: .infinity)
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text(definition?.title ?? "Deleted alarm")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(finished ? Theme.secondaryText : Theme.ink)
                    .strikethrough(occurrence.status == .completed)
                    .multilineTextAlignment(.leading)
                badges
            }
            .padding(.top, 3)
            .padding(.bottom, 18)
            Spacer(minLength: 0)
        }
        .fixedSize(horizontal: false, vertical: true)
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private var marker: some View {
        switch occurrence.status {
        case .completed:
            Image(systemName: "checkmark.circle.fill").resizable().foregroundStyle(Theme.violet)
        case .dismissed:
            Image(systemName: "xmark.circle.fill").resizable().foregroundStyle(Theme.mist)
        case .snoozed:
            Circle().strokeBorder(Theme.nag, lineWidth: 3)
        case .pending, .active:
            if definition?.isEvent == true {
                Circle().strokeBorder(Theme.violet, lineWidth: 3)
            } else {
                Circle().fill(Theme.violet)
            }
        }
    }

    private var badges: some View {
        HStack(spacing: 6) {
            if occurrence.status == .snoozed {
                Badge(symbol: "zzz", text: "Snoozed", tint: Theme.nag)
            }
            if definition?.isEvent == true {
                Badge(symbol: "calendar", text: "Starts \(Formatting.time(occurrence.scheduledFor))")
            }
            if let deadline = occurrence.deadline, !finished {
                Badge(symbol: "flag.fill", text: "By \(Formatting.time(deadline))", tint: Theme.nag)
            }
            if definition?.isRecurring == true {
                Badge(symbol: "repeat")
            }
            if definition?.important == true {
                Badge(symbol: "bell.badge.fill", tint: Theme.violet)
            }
            switch definition?.devices {
            case .desktop: Badge(symbol: "desktopcomputer")
            case .phone: Badge(symbol: "iphone")
            default: EmptyView()
            }
        }
    }
}

/// When the phone last synced with the PC, or why it couldn't, floating above the content.
private struct SyncStatusPill: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            switch model.syncStatus {
            case .notSetUp:
                Label("Link your PC in Settings to sync", systemImage: "link")
            case .syncing:
                Label("Syncing with your PC", systemImage: "arrow.triangle.2.circlepath")
            case .idle(let lastSynced):
                if let lastSynced {
                    Label("Synced \(lastSynced.formatted(.relative(presentation: .named)))", systemImage: "checkmark.circle")
                } else {
                    Label("Not synced yet", systemImage: "arrow.triangle.2.circlepath")
                }
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle")
            }
        }
        .font(.footnote.weight(.medium))
        .foregroundStyle(Theme.secondaryText)
        .lineLimit(2)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.ultraThinMaterial, in: Capsule())
        .padding(.bottom, 6)
    }
}


/// The app icon in miniature: the clock face on its gradient tile, without the dial's progress ring.
struct AppMark: View {
    var body: some View {
        GeometryReader { geometry in
            let size = min(geometry.size.width, geometry.size.height)
            RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
                .fill(Theme.brand)
                .overlay {
                    ClockDial(time: WallTime(year: 2026, month: 1, day: 1, hour: 2, minute: 10), ringColor: .clear)
                        .padding(size * 0.08)
                }
        }
        .aspectRatio(1, contentMode: .fit)
        .accessibilityHidden(true)
    }
}
