import Foundation
import NagsterCore

/// How times read in the app and its notifications, e.g. "Today 09:00" or "Wed 22 Oct 09:00".
enum Formatting {
    static func time(_ time: WallTime) -> String {
        time.date().formatted(date: .omitted, time: .shortened)
    }

    static func dayAndTime(_ time: WallTime) -> String {
        "\(day(time)) \(self.time(time))"
    }

    /// A day heading: "Today", "Tomorrow" or e.g. "Wednesday 22 October".
    static func longDay(_ time: WallTime) -> String {
        let date = time.date()
        let calendar = Calendar.current
        if calendar.isDateInToday(date) { return "Today" }
        if calendar.isDateInTomorrow(date) { return "Tomorrow" }
        return date.formatted(.dateTime.weekday(.wide).day().month(.wide))
    }

    static func day(_ time: WallTime) -> String {
        let calendar = Calendar.current
        let date = time.date()
        if calendar.isDateInToday(date) { return "Today" }
        if calendar.isDateInTomorrow(date) { return "Tomorrow" }
        if calendar.isDateInYesterday(date) { return "Yesterday" }
        return date.formatted(.dateTime.weekday(.abbreviated).day().month(.abbreviated))
    }
}
