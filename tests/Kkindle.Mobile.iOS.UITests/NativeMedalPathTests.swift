import CoreGraphics
import Foundation

// Run from the repository root:
// swiftc src/Kkindle.Mobile.iOS/NativeUI/NativeMedalPath.swift tests/Kkindle.Mobile.iOS.UITests/NativeMedalPathTests.swift -o /tmp/kkindle-medal-path-tests && /tmp/kkindle-medal-path-tests src/Kkindle.Core/ReadingMedalCatalog.cs
@main
struct NativeMedalPathTests {
    static var checks = 0

    static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        checks += 1
        if !condition() {
            fputs("FAIL: \(message)\n", stderr)
            exit(1)
        }
    }

    static func near(_ actual: CGFloat, _ expected: CGFloat, _ tolerance: CGFloat = 0.001) -> Bool {
        abs(actual - expected) <= tolerance
    }

    static func bounds(_ text: String) -> CGRect {
        guard let path = NativeMedalPath.parse(text) else {
            fputs("FAIL: Could not parse \(text)\n", stderr)
            exit(1)
        }
        return path.boundingBoxOfPath
    }

    static func firstCurveMidpoint(_ text: String) -> CGPoint? {
        guard let path = NativeMedalPath.parse(text) else { return nil }
        var start = CGPoint.zero
        var midpoint: CGPoint?
        path.applyWithBlock { element in
            let item = element.pointee
            if item.type == .moveToPoint {
                start = item.points[0]
            } else if item.type == .addCurveToPoint && midpoint == nil {
                let c1 = item.points[0], c2 = item.points[1], end = item.points[2]
                midpoint = CGPoint(x: (start.x + 3 * c1.x + 3 * c2.x + end.x) / 8,
                                   y: (start.y + 3 * c1.y + 3 * c2.y + end.y) / 8)
            }
        }
        return midpoint
    }

    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            fputs("Usage: NativeMedalPathTests <ReadingMedalCatalog.cs>\n", stderr)
            exit(2)
        }
        let catalog = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
        let regex = try NSRegularExpression(pattern: #""(M[0-9., +eE\-MLHVQCAZ\s]+)""#)
        let nsCatalog = catalog as NSString
        let paths = regex.matches(in: catalog, range: NSRange(location: 0, length: nsCatalog.length))
            .map { nsCatalog.substring(with: $0.range(at: 1)) }
        check(paths.count == 40, "expected 40 paths from the actual catalog, found \(paths.count)")
        for (index, text) in paths.enumerated() {
            guard let path = NativeMedalPath.parse(text) else {
                check(false, "catalog path \(index + 1) failed: \(text)")
                continue
            }
            let rect = path.boundingBoxOfPath
            check(!path.isEmpty && !rect.isNull && !rect.isInfinite &&
                  [rect.minX, rect.minY, rect.maxX, rect.maxY].allSatisfy(\.isFinite) &&
                  rect.width > 0 && rect.height > 0, "invalid catalog bounds at path \(index + 1): \(rect)")
        }

        let circle = bounds("M10 0 A10 10 0 1 1 -10 0 A10 10 0 1 1 10 0")
        check(near(circle.minX, -10) && near(circle.maxX, 10) &&
              near(circle.minY, -10) && near(circle.maxY, 10), "two arcs form a full circle: \(circle)")
        let quarter = bounds("M1 0 A1 1 0 0 1 0 1")
        check(near(quarter.minX, 0) && near(quarter.maxX, 1) &&
              near(quarter.minY, 0) && near(quarter.maxY, 1), "small sweep arc: \(quarter)")
        let sweepMidpoint = firstCurveMidpoint("M1 0 A1 1 0 0 1 0 1")
        let reverseMidpoint = firstCurveMidpoint("M1 0 A1 1 0 0 0 0 1")
        check(sweepMidpoint != nil && near(sweepMidpoint!.x, sqrt(0.5)) && near(sweepMidpoint!.y, sqrt(0.5)),
              "small sweep follows the expected circle")
        check(reverseMidpoint != nil && near(reverseMidpoint!.x, 1 - sqrt(0.5)) && near(reverseMidpoint!.y, 1 - sqrt(0.5)),
              "reverse sweep follows the other circle")
        let largeSweep = bounds("M1 0 A1 1 0 1 1 0 1")
        check(near(largeSweep.minX, 0) && near(largeSweep.maxX, 2) &&
              near(largeSweep.minY, 0) && near(largeSweep.maxY, 2), "large sweep arc: \(largeSweep)")
        let largeReverse = bounds("M1 0 A1 1 0 1 0 0 1")
        check(near(largeReverse.minX, -1) && near(largeReverse.maxX, 1) &&
              near(largeReverse.minY, -1) && near(largeReverse.maxY, 1), "large reverse arc: \(largeReverse)")
        let reverse = bounds("M1 0 A1 1 0 0 0 0 1")
        check(near(reverse.minX, 0) && near(reverse.maxX, 1) &&
              near(reverse.minY, 0) && near(reverse.maxY, 1), "small reverse arc: \(reverse)")
        let corrected = bounds("M0 0 A1 1 0 0 1 4 0")
        check(near(corrected.width, 4) && near(corrected.height, 2), "radii are expanded to reach the endpoint: \(corrected)")
        let rotated = bounds("M0 0 A10 5 90 0 1 0 20")
        check(near(rotated.width, 5) && near(rotated.height, 20), "rotated ellipse: \(rotated)")
        let line = bounds("M0 0 A0 5 0 0 1 4 3")
        check(near(line.width, 4) && near(line.height, 3), "zero-radius arc becomes a line")
        let same = bounds("M0 0 A5 5 0 1 1 0 0 L2 3")
        check(near(same.width, 2) && near(same.height, 3), "coincident endpoints add no arc")
        let compact = bounds("M.5-.5 2 3 H4 5 V6 7 Q6 8 7 9 8 10 9 11 C1 2 3 4 5 6 7 8 9 10 11 12 A2 3 0 0113 14 Z")
        check(!compact.isNull && compact.maxX >= 13 && compact.maxY >= 14, "compact syntax and repeated argument groups")
        let exponent = bounds("M1e1,-2E+0 L.5,3. H-1 V4")
        check(near(exponent.maxX, 10) && near(exponent.minY, -2), "signed, decimal and exponent numbers")
        let closed = bounds("M1 2 L3 4 Z H5")
        check(near(closed.minY, 2) && near(closed.maxY, 4) && near(closed.maxX, 5), "close restores current point")

        let invalid = [
            "", " ", "L0 0", "Z", "M", "M0", "M0 0 L", "M0 0 Q1 2 3", "M0 0 C1 2 3 4 5",
            "M,0 0", "M0,,0", "M0 0,", "M0 0,L1 1", "M0 0 L1 1, Z", "M0 0 Z 1 2",
            "M0 0 l1 2", "M0 0 X1 2", "M0 0 !", "MNaN 0", "MInf 0", "M1e999 0", "M1e 0",
            "M0 0 A-1 1 0 0 1 2 2", "M0 0 A1 -1 0 0 1 2 2", "M0 0 A1 1 0 2 1 2 2",
            "M0 0 A1 1 0 0 -1 2 2", "M0 0 A1 1 0 0 1 2", "M0 0 A1 1 0 0 1 1e308 1e308",
            "M-1e308 0 L1e308 0", "M0 0\u{00a0}L1 1"
        ]
        for text in invalid {
            check(NativeMedalPath.parse(text) == nil, "must reject invalid input: \(text)")
        }
        print("PASS: \(paths.count) catalog paths; \(checks) checks")
    }
}
