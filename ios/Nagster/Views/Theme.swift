import SwiftUI
import UIKit

/// Nagster's look, taken from its icon: a white clock face with indigo hands on a violet gradient. Coral is kept
/// for urgency (ringing alarms, deadlines) so it always means something.
enum Theme {
    /// Text and the clock's hands; near-white on the dark canvas.
    static let ink = Color(light: 0x302878, dark: 0xECEAFF)
    /// The hands on the dial, which is always light.
    static let dialInk = Color(hex: 0x302878)
    static let violet = Color(hex: 0x7C6CF2)
    static let lilac = Color(hex: 0x8E80F4)
    static let deep = Color(hex: 0x5846D6)
    static let face = Color(hex: 0xFAFAFF)
    static let mist = Color(hex: 0x968CD2)
    static let nag = Color(hex: 0xFF5C7A)
    static let canvas = Color(light: 0xF6F5FE, dark: 0x15122F)
    /// The timeline's rail and other hairlines.
    static let rail = Color(light: 0xDCD7FB, dark: 0x3A3370)
    static let secondaryText = Color(light: 0x6B65A3, dark: 0xA9A3DA)

    static let brand = LinearGradient(colors: [lilac, deep], startPoint: .topLeading, endPoint: .bottomTrailing)
    /// The hero while an alarm is ringing: the brand gradient warmed towards coral.
    static let nagging = LinearGradient(colors: [Color(hex: 0xFF7A90), Color(hex: 0x7A4BE0)],
                                        startPoint: .topLeading, endPoint: .bottomTrailing)

    /// Sets the navigation bar titles in SF Rounded and Nagster's ink, to match the times.
    static func styleNavigationBars() {
        let ink = UIColor(ink)
        func rounded(_ size: CGFloat, _ weight: UIFont.Weight) -> UIFont {
            let base = UIFont.systemFont(ofSize: size, weight: weight)
            return base.fontDescriptor.withDesign(.rounded).map { UIFont(descriptor: $0, size: size) } ?? base
        }
        let appearance = UINavigationBar.appearance()
        appearance.largeTitleTextAttributes = [.font: rounded(34, .bold), .foregroundColor: ink]
        appearance.titleTextAttributes = [.font: rounded(17, .semibold), .foregroundColor: ink]
    }
}

extension Font {
    /// SF Rounded at a Dynamic Type style, for times and headings.
    static func rounded(_ style: Font.TextStyle, weight: Font.Weight = .semibold) -> Font {
        .system(style, design: .rounded, weight: weight)
    }
}

extension Color {
    init(hex: UInt32) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }

    /// A colour that follows light and dark mode.
    init(light: UInt32, dark: UInt32) {
        self.init(uiColor: Self.dynamicColor(light: light, dark: dark))
    }

    /// UIKit may resolve dynamic colours off the main thread, so the provider is built in a nonisolated function:
    /// a closure made in main-actor code would trap when called elsewhere.
    nonisolated private static func dynamicColor(light: UInt32, dark: UInt32) -> UIColor {
        UIColor { traits in
            let hex = traits.userInterfaceStyle == .dark ? dark : light
            return UIColor(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                           blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        }
    }
}

/// A small coloured label for a row: a symbol with optional text, e.g. a deadline or "snoozed".
struct Badge: View {
    var symbol: String
    var text: String?
    var tint: Color = Theme.secondaryText

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: symbol)
            if let text { Text(text) }
        }
        .font(.caption.weight(.medium))
        .padding(.horizontal, text == nil ? 6 : 8)
        .padding(.vertical, 3)
        .foregroundStyle(tint)
        .background(tint.opacity(0.13), in: Capsule())
    }
}
