import CoreGraphics

/// Where the popup hangs under the menu-bar icon. Cocoa coordinates: the origin is bottom left, y grows up.
/// Kept apart from AppKit so the arithmetic is testable.
public enum PopupPlacement {
    private static let gap: CGFloat = 4
    private static let edge: CGFloat = 8
    /// Roughly a menu bar plus a gap. A popup never hangs lower or higher than this from the top of the screen.
    private static let barHeight: CGFloat = 28

    /// The frame for a popup of `size` opened from `icon`. The icon's frame can lie above the screen: in a full-screen
    /// app the menu bar slides away, and an anchor taken from it then would put the top of the popup off screen.
    public static func frame(icon: CGRect, size: CGSize, screen: CGRect, visible: CGRect) -> CGRect {
        var x = icon.midX - size.width / 2
        x = min(max(x, visible.minX + edge), visible.maxX - size.width - edge)
        let top = min(icon.minY - gap, screen.maxY - barHeight)
        return CGRect(x: x, y: top - size.height, width: size.width, height: size.height)
    }

    /// The same popup at a new size: the top edge and left edge stay where they are, so growing downwards never
    /// moves it. (Re-anchoring to the icon on every resize is what made it jump when the menu bar had slid away.)
    public static func resized(_ frame: CGRect, to size: CGSize) -> CGRect {
        CGRect(x: frame.minX, y: frame.maxY - size.height, width: size.width, height: size.height)
    }
}
