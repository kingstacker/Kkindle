import CoreGraphics
import Foundation

/// Parses the absolute SVG path commands used by the reading medal catalog.
enum NativeMedalPath {
    static func parse(_ text: String) -> CGPath? {
        var parser = Parser(text)
        return parser.parse()
    }

    private struct Parser {
        private let bytes: [UInt8]
        private var index = 0
        private let path = CGMutablePath()
        private var current = CGPoint.zero
        private var subpath = CGPoint.zero
        private var hasMove = false
        private var command: UInt8?

        init(_ text: String) {
            bytes = Array(text.utf8)
        }

        private mutating func skipWhitespace() {
            while index < bytes.count && [9, 10, 13, 32].contains(bytes[index]) {
                index += 1
            }
        }

        // A comma may only occur between two arguments, never after a command.
        private mutating func prepareArgument(_ allowComma: Bool) -> Bool {
            skipWhitespace()
            if index < bytes.count && bytes[index] == 44 {
                guard allowComma else { return false }
                index += 1
                skipWhitespace()
            }
            return index < bytes.count
        }

        private mutating func number(_ allowComma: Bool) -> CGFloat? {
            guard prepareArgument(allowComma) else { return nil }
            let start = index
            if index < bytes.count && (bytes[index] == 43 || bytes[index] == 45) { index += 1 }
            var digits = 0
            while index < bytes.count && (48...57).contains(bytes[index]) {
                digits += 1
                index += 1
            }
            if index < bytes.count && bytes[index] == 46 {
                index += 1
                while index < bytes.count && (48...57).contains(bytes[index]) {
                    digits += 1
                    index += 1
                }
            }
            guard digits > 0 else { return nil }
            if index < bytes.count && (bytes[index] == 69 || bytes[index] == 101) {
                index += 1
                if index < bytes.count && (bytes[index] == 43 || bytes[index] == 45) { index += 1 }
                let exponentStart = index
                while index < bytes.count && (48...57).contains(bytes[index]) { index += 1 }
                guard index > exponentStart else { return nil }
            }
            guard let value = Double(String(decoding: bytes[start..<index], as: UTF8.self)), value.isFinite else {
                return nil
            }
            let result = CGFloat(value)
            return result.isFinite ? result : nil
        }

        private mutating func flag() -> Bool? {
            guard prepareArgument(true), bytes[index] == 48 || bytes[index] == 49 else { return nil }
            let value = bytes[index] == 49
            index += 1
            return value
        }

        mutating func parse() -> CGPath? {
            while true {
                skipWhitespace()
                if index == bytes.count { break }
                var explicit = false
                let byte = bytes[index]
                if [77, 76, 72, 86, 81, 67, 65, 90].contains(byte) {
                    command = byte
                    index += 1
                    explicit = true
                    if byte == 90 {
                        guard hasMove else { return nil }
                        path.closeSubpath()
                        current = subpath
                        command = nil
                        continue
                    }
                }
                guard let op = command, hasMove || op == 77 else { return nil }
                // An explicit command is followed by its first argument without a comma;
                // implicit argument groups may be separated from the previous one by a comma.
                guard let a = number(!explicit) else { return nil }
                switch op {
                case 77, 76:
                    guard let b = number(true) else { return nil }
                    let point = CGPoint(x: a, y: b)
                    if op == 77 {
                        path.move(to: point)
                        subpath = point
                        hasMove = true
                        command = 76 // Subsequent coordinate pairs after M are lines.
                    } else {
                        path.addLine(to: point)
                    }
                    current = point
                case 72:
                    current = CGPoint(x: a, y: current.y)
                    path.addLine(to: current)
                case 86:
                    current = CGPoint(x: current.x, y: a)
                    path.addLine(to: current)
                case 81:
                    guard let b = number(true), let c = number(true), let d = number(true) else { return nil }
                    let end = CGPoint(x: c, y: d)
                    path.addQuadCurve(to: end, control: CGPoint(x: a, y: b))
                    current = end
                case 67:
                    guard let b = number(true), let c = number(true), let d = number(true),
                          let e = number(true), let f = number(true) else { return nil }
                    let end = CGPoint(x: e, y: f)
                    path.addCurve(to: end, control1: CGPoint(x: a, y: b), control2: CGPoint(x: c, y: d))
                    current = end
                case 65:
                    guard let ry = number(true), let rotation = number(true),
                          let large = flag(), let sweep = flag(),
                          let x = number(true), let y = number(true), a >= 0, ry >= 0 else { return nil }
                    let end = CGPoint(x: x, y: y)
                    guard addArc(from: current, to: end, rx: a, ry: ry,
                                 rotation: rotation, large: large, sweep: sweep) else { return nil }
                    current = end
                default:
                    return nil
                }
            }
            guard hasMove, !path.isEmpty else { return nil }
            let bounds = path.boundingBoxOfPath
            guard !bounds.isNull, !bounds.isInfinite, bounds.origin.x.isFinite, bounds.origin.y.isFinite,
                  bounds.width.isFinite, bounds.height.isFinite else { return nil }
            return path.copy()
        }

        // SVG endpoint-to-center conversion, followed by cubic segments of at most 90 degrees.
        private func addArc(from start: CGPoint, to end: CGPoint, rx originalRX: CGFloat, ry originalRY: CGFloat,
                            rotation: CGFloat, large: Bool, sweep: Bool) -> Bool {
            if start == end { return true }
            if originalRX == 0 || originalRY == 0 {
                path.addLine(to: end)
                return true
            }
            let angle = rotation.truncatingRemainder(dividingBy: 360) * .pi / 180
            let cosAngle = cos(angle), sinAngle = sin(angle)
            let dx = start.x / 2 - end.x / 2, dy = start.y / 2 - end.y / 2
            let xp = cosAngle * dx + sinAngle * dy
            let yp = -sinAngle * dx + cosAngle * dy
            var rx = originalRX, ry = originalRY
            var ux = xp / rx, uy = yp / ry
            let scale = hypot(ux, uy)
            guard scale.isFinite else { return false }
            if scale > 1 {
                rx *= scale
                ry *= scale
                ux = xp / rx
                uy = yp / ry
            }
            let magnitude = hypot(ux, uy)
            guard rx.isFinite, ry.isFinite, magnitude.isFinite, magnitude > 0 else { return false }
            let factor = (large == sweep ? -1.0 : 1.0) * sqrt(max(0, 1 - magnitude * magnitude)) / magnitude
            let cxp = rx * (factor * uy), cyp = ry * (-factor * ux)
            let cx = cosAngle * cxp - sinAngle * cyp + start.x / 2 + end.x / 2
            let cy = sinAngle * cxp + cosAngle * cyp + start.y / 2 + end.y / 2
            let v1x = (xp - cxp) / rx, v1y = (yp - cyp) / ry
            let v2x = (-xp - cxp) / rx, v2y = (-yp - cyp) / ry
            let startAngle = atan2(v1y, v1x)
            var delta = atan2(v1x * v2y - v1y * v2x, v1x * v2x + v1y * v2y)
            if sweep && delta < 0 { delta += 2 * .pi }
            if !sweep && delta > 0 { delta -= 2 * .pi }
            guard [cx, cy, startAngle, delta].allSatisfy(\.isFinite) else { return false }
            let count = max(1, Int(ceil(abs(delta) / (.pi / 2))))
            let step = delta / CGFloat(count)
            func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
                CGPoint(x: cx + rx * cosAngle * x - ry * sinAngle * y,
                        y: cy + rx * sinAngle * x + ry * cosAngle * y)
            }
            for i in 0..<count {
                let t1 = startAngle + CGFloat(i) * step, t2 = t1 + step
                let alpha = 4 / 3 * tan(step / 4)
                let c1 = point(cos(t1) - alpha * sin(t1), sin(t1) + alpha * cos(t1))
                let c2 = point(cos(t2) + alpha * sin(t2), sin(t2) - alpha * cos(t2))
                let target = i == count - 1 ? end : point(cos(t2), sin(t2))
                guard [c1.x, c1.y, c2.x, c2.y, target.x, target.y].allSatisfy(\.isFinite) else { return false }
                path.addCurve(to: target, control1: c1, control2: c2)
            }
            return true
        }
    }
}
