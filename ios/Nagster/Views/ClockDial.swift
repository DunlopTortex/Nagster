import NagsterCore
import SwiftUI

/// The app icon's clock, brought to life: the hands show an alarm's time and the ring around the face fills as it
/// approaches. While an alarm is ringing the ring turns coral and the dial gently pulses (not with Reduce Motion).
struct ClockDial: View {
    /// The time the hands show; nil leaves the face without hands.
    var time: WallTime?
    /// How full the outer ring is, 0 to 1.
    var progress: Double = 0
    var urgent = false
    /// The ring's track and fill; white on the gradient hero, violet on plain backgrounds.
    var ringColor: Color = .white

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var pulsing = false

    var body: some View {
        GeometryReader { geometry in
            let size = min(geometry.size.width, geometry.size.height)
            let ring = size * 0.075
            let faceInset = ring * 1.7
            ZStack {
                if urgent {
                    Circle()
                        .fill(Theme.nag.opacity(0.35))
                        .scaleEffect(pulsing ? 1.14 : 1)
                        .opacity(pulsing ? 0 : 0.9)
                }
                Circle().stroke(ringColor.opacity(0.25), lineWidth: ring)
                Circle()
                    .trim(from: 0, to: min(max(progress, 0), 1))
                    .stroke(urgent ? Theme.nag : ringColor, style: StrokeStyle(lineWidth: ring, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Circle().fill(Theme.face).padding(faceInset)
                ForEach(0..<4, id: \.self) { quarter in
                    Capsule()
                        .fill(Theme.mist)
                        .frame(width: size * 0.028, height: size * 0.07)
                        .offset(y: -(size / 2 - faceInset - size * 0.075))
                        .rotationEffect(.degrees(Double(quarter) * 90))
                }
                if let time, let hour = time.components.hour, let minute = time.components.minute {
                    hand(length: size * 0.19, width: size * 0.055, degrees: (Double(hour % 12) + Double(minute) / 60) * 30)
                    hand(length: size * 0.27, width: size * 0.045, degrees: Double(minute) * 6)
                }
                Circle().fill(Theme.dialInk).frame(width: size * 0.08, height: size * 0.08)
            }
            .frame(width: size, height: size)
            .position(x: geometry.size.width / 2, y: geometry.size.height / 2)
        }
        .aspectRatio(1, contentMode: .fit)
        .task(id: urgent) {
            pulsing = false
            guard urgent, !reduceMotion else { return }
            withAnimation(.easeOut(duration: 1.4).repeatForever(autoreverses: false)) { pulsing = true }
        }
        .accessibilityHidden(true)
    }

    /// A hand pointing at `degrees` clockwise from twelve.
    private func hand(length: CGFloat, width: CGFloat, degrees: Double) -> some View {
        Capsule()
            .fill(Theme.dialInk)
            .frame(width: width, height: length)
            .offset(y: -length / 2)
            .rotationEffect(.degrees(degrees))
    }
}

#Preview {
    HStack(spacing: 24) {
        ClockDial(time: WallTime(year: 2026, month: 10, day: 20, hour: 2, minute: 10), progress: 0.6)
        ClockDial(time: WallTime(year: 2026, month: 10, day: 20, hour: 9, minute: 0), progress: 1, urgent: true)
    }
    .padding(40)
    .background(Theme.brand)
}
