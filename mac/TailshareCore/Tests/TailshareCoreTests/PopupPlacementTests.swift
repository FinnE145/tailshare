import CoreGraphics
import XCTest
@testable import TailshareCore

final class PopupPlacementTests: XCTestCase {
    private let screen = CGRect(x: 0, y: 0, width: 1440, height: 900)
    private let visible = CGRect(x: 0, y: 0, width: 1440, height: 876)
    private let size = CGSize(width: 340, height: 500)

    // spec B Menu-bar icon and popup: on the desktop the popup hangs under the icon, centred on it, a few points below the bar.
    func testHangsUnderTheIconCentred() {
        let icon = CGRect(x: 1000, y: 876, width: 24, height: 24)
        XCTAssertEqual(PopupPlacement.frame(icon: icon, size: size, screen: screen, visible: visible),
                       CGRect(x: 842, y: 872 - 500, width: 340, height: 500))
    }

    // spec B Menu-bar icon and popup: near the right edge it is kept on screen, with a margin.
    func testStaysOnScreenHorizontally() {
        let right = CGRect(x: 1416, y: 876, width: 24, height: 24)
        XCTAssertEqual(PopupPlacement.frame(icon: right, size: size, screen: screen, visible: visible).maxX, 1432)
        let left = CGRect(x: 0, y: 876, width: 24, height: 24)
        XCTAssertEqual(PopupPlacement.frame(icon: left, size: size, screen: screen, visible: visible).minX, 8)
    }

    // spec B Menu-bar icon and popup: when the icon is above the screen (the menu bar slid away in a full-screen app),
    // the popup's top is still on the screen, not off the top of it. A taller bar (a notch) is followed, not clamped.
    func testIconAboveTheScreenDoesNotPushThePopupOffIt() {
        let hidden = CGRect(x: 1000, y: 905, width: 24, height: 24)
        XCTAssertEqual(PopupPlacement.frame(icon: hidden, size: size, screen: screen, visible: screen).maxY, 872)

        let notch = CGRect(x: 1000, y: 862, width: 24, height: 38)
        XCTAssertEqual(PopupPlacement.frame(icon: notch, size: size, screen: screen, visible: visible).maxY, 858)
    }

    // spec B Menu-bar icon and popup: growing or shrinking keeps the top and left edges where they were.
    func testResizeKeepsTopAndLeft() {
        let opened = CGRect(x: 842, y: 372, width: 340, height: 500)
        XCTAssertEqual(PopupPlacement.resized(opened, to: CGSize(width: 340, height: 560)), CGRect(x: 842, y: 312, width: 340, height: 560))
        XCTAssertEqual(PopupPlacement.resized(opened, to: CGSize(width: 340, height: 420)), CGRect(x: 842, y: 452, width: 340, height: 420))
    }
}
