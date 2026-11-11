import SwiftUI
import UIKit
import ImageIO

/// Small RGB values can cross the background decoding task without carrying UI objects.
struct NativeAmbientColor: Equatable {
    let red: Double
    let green: Double
    let blue: Double
    var x: Double? = nil
    var y: Double? = nil
    var color: Color { Color(red: red, green: green, blue: blue) }
}

enum NativeAmbientPalette {
    /// One bucket of the cover thumbnail, kept in normalised RGB plus its hue.
    private struct Candidate {
        let red: Double
        let green: Double
        let blue: Double
        let hue: Double
        let saturation: Double
        let brightness: Double
        let count: Int

        /// Prefer plenty of colour over plenty of pixels; the vivid pass
        /// excludes paper and ink before scoring.
        var score: Double {
            Double(count).squareRoot() * (0.35 + saturation * 1.15) * (0.55 + brightness * 0.6)
        }

        /// Covers are often printed in shadow. Lift the sample into a pastel
        /// range so the wash reads as colour instead of turning muddy.
        var vividColor: NativeAmbientColor {
            let saturation = min(1, max(0.62, self.saturation * 1.35))
            let brightness = min(0.95, max(0.58, self.brightness * 1.15 + 0.05))
            let rgb = NativeAmbientPalette.rgb(hue: hue, saturation: saturation, brightness: brightness)
            return NativeAmbientColor(red: rgb.red, green: rgb.green, blue: rgb.blue)
        }

        /// Used only when the whole shelf is black and white, where forcing
        /// saturation would invent a hue the books don't have.
        var mutedColor: NativeAmbientColor {
            let peak = max(red, green, blue)
            let scale = peak == 0 ? 1 : min(2.5, 0.75 / peak)
            return NativeAmbientColor(red: min(1, red * scale), green: min(1, green * scale), blue: min(1, blue * scale))
        }
    }

    /// Read small thumbnails from actual library covers; no cover image is retained.
    static func extract(paths: [String]) -> [NativeAmbientColor] {
        var picked: [NativeAmbientColor] = []
        for path in paths {
            if picked.count == 6 { break }
            guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: 32
                  ] as CFDictionary) else { continue }
            let size = 24
            var pixels = [UInt8](repeating: 0, count: size * size * 4)
            let rendered = pixels.withUnsafeMutableBytes { bytes -> Bool in
                guard let context = CGContext(data: bytes.baseAddress, width: size, height: size,
                    bitsPerComponent: 8, bytesPerRow: size * 4, space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue) else { return false }
                context.draw(image, in: CGRect(x: 0, y: 0, width: size, height: size))
                return true
            }
            guard rendered else { continue }
            var buckets: [Int: (count: Int, red: Int, green: Int, blue: Int)] = [:]
            for index in stride(from: 0, to: pixels.count, by: 4) {
                guard pixels[index + 3] > 200 else { continue }
                let r = Int(pixels[index]), g = Int(pixels[index + 1]), b = Int(pixels[index + 2])
                // Keep neutral samples for genuinely black-and-white covers;
                // the vivid candidate pass below still prefers coloured artwork.
                let key = (r / 32) * 64 + (g / 32) * 8 + b / 32
                let old = buckets[key] ?? (0, 0, 0, 0)
                buckets[key] = (old.count + 1, old.red + r, old.green + g, old.blue + b)
            }
            let candidates: [Candidate] = buckets.values.map { bucket in
                let divisor = Double(bucket.count) * 255
                let red = Double(bucket.red) / divisor
                let green = Double(bucket.green) / divisor
                let blue = Double(bucket.blue) / divisor
                let high = max(red, green, blue), low = min(red, green, blue)
                let saturation = high == 0 ? 0 : (high - low) / high
                return Candidate(red: red, green: green, blue: blue,
                                 hue: hue(red: red, green: green, blue: blue),
                                 saturation: saturation, brightness: high, count: bucket.count)
            }
            let vivid = candidates.filter { $0.saturation >= 0.30 && $0.brightness >= 0.18 }
            if let best = vivid.max(by: { $0.score < $1.score }) {
                picked.append(best.vividColor)
            } else if let quiet = candidates.max(by: { $0.score < $1.score }) {
                picked.append(quiet.mutedColor)
            }
        }
        // Preserve cover order so each sample can stay next to its source book.
        return picked
    }

    private static func hue(red: Double, green: Double, blue: Double) -> Double {
        let high = max(red, green, blue), low = min(red, green, blue)
        let delta = high - low
        guard delta > 0 else { return 0 }
        var value: Double
        if high == red { value = ((green - blue) / delta).truncatingRemainder(dividingBy: 6) }
        else if high == green { value = (blue - red) / delta + 2 }
        else { value = (red - green) / delta + 4 }
        value /= 6
        return value < 0 ? value + 1 : value
    }

    private static func rgb(hue: Double, saturation: Double, brightness: Double) -> (red: Double, green: Double, blue: Double) {
        let position = (hue - floor(hue)) * 6
        let sector = Int(floor(position)) % 6
        let fraction = position - floor(position)
        let p = brightness * (1 - saturation)
        let q = brightness * (1 - saturation * fraction)
        let t = brightness * (1 - saturation * (1 - fraction))
        switch sector {
        case 0: return (brightness, t, p)
        case 1: return (q, brightness, p)
        case 2: return (p, brightness, t)
        case 3: return (p, q, brightness)
        case 4: return (t, p, brightness)
        default: return (brightness, p, q)
        }
    }
}

/// Blurred fields are generated from sampled colours, never from book text or covers.
struct NativeAmbientWash: View {
    @EnvironmentObject private var store: NativeStore
    var colorsOverride: [NativeAmbientColor]? = nil
    var body: some View {
        GeometryReader { geometry in
            let colors = colorsOverride ?? store.ambientColors
            if !colors.isEmpty {
                let radius = max(geometry.size.width * 0.55,
                                 geometry.size.height / CGFloat(max(colors.count, 2)) * 1.2)
                let middle = colors[colors.count / 2].color
                ZStack {
                    // A low-opacity base keeps the corners and the gaps between
                    // the radial fields tinted instead of fading to plain white.
                    LinearGradient(stops: [
                        .init(color: colors[0].color.opacity(0.12), location: 0),
                        .init(color: middle.opacity(0.10), location: 0.5),
                        .init(color: colors[colors.count - 1].color.opacity(0.12), location: 1)
                    ], startPoint: .topLeading, endPoint: .bottomTrailing)
                    ForEach(Array(colors.enumerated()), id: \.offset) { index, color in
                        Circle().fill(RadialGradient(gradient: Gradient(stops: [
                            .init(color: color.color.opacity(0.9), location: 0),
                            .init(color: color.color.opacity(0.62), location: 0.55),
                            .init(color: color.color.opacity(0), location: 1)
                        ]), center: .center, startRadius: 0, endRadius: radius))
                            .frame(width: radius * 2, height: radius * 2)
                            .position(x: geometry.size.width * CGFloat(color.x ?? (index.isMultiple(of: 2) ? 0.25 : 0.75)),
                                      y: geometry.size.height * CGFloat(color.y ?? Double(index + 1) / Double(colors.count + 1)))
                    }
                }.frame(width: geometry.size.width, height: geometry.size.height).clipped()
            }
        }.allowsHitTesting(false).accessibilityHidden(true)
    }
}

struct NativeAmbientBackground: View {
    @EnvironmentObject private var store: NativeStore
    @Environment(\.colorScheme) private var scheme
    let fallback: Color
    let colorsOverride: [NativeAmbientColor]?
    init(fallback: Color = NativeTheme.page, colorsOverride: [NativeAmbientColor]? = nil) {
        self.fallback = fallback; self.colorsOverride = colorsOverride
    }
    var body: some View {
        let colors = colorsOverride ?? store.ambientColors
        ZStack {
            fallback
            if store.preferences.colorfulMode && !colors.isEmpty {
                NativeAmbientWash(colorsOverride: colors).opacity(scheme == .dark ? 0.36 : 0.34)
            }
        }.animation(.easeInOut(duration: 0.4), value: colors.map {
            NativeAmbientColor(red: $0.red, green: $0.green, blue: $0.blue)
        })
            .ignoresSafeArea().allowsHitTesting(false).accessibilityHidden(true)
    }
}
