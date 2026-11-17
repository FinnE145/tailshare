import AppKit
import Combine
import SwiftUI
import TailshareCore

/// The menu-bar icon and the popup under it. Not SwiftUI's `MenuBarExtra`: that closes whenever focus moves
/// (so a file browser, or a file dragged in from Finder, dismissed it) and re-anchors itself to the icon on every
/// resize (so it jumped when a full-screen app's menu bar had slid away).
@MainActor
final class PopupController: NSObject {
    private let model: AppModel
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let host: NSHostingController<MenuBarView>
    private let panel: PopupPanel
    private var monitors: [Any] = []
    private var iconObservation: AnyCancellable?
    private var sizeObservation: NSKeyValueObservation?

    // For the outside-click rule below.
    private var pendingOutsideClick = false
    private var dragChangeCountAtMouseDown = 0

    init(model: AppModel) {
        self.model = model
        host = NSHostingController(rootView: MenuBarView(model: model))
        host.sizingOptions = [.preferredContentSize]
        panel = PopupPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 400))
        super.init()

        let background = NSVisualEffectView()
        background.material = .popover
        background.state = .active
        // The window is a rectangle whatever the layer of its content does; a mask image is what rounds a
        // visual-effect view and its shadow.
        background.maskImage = Self.roundedMask(radius: 10)
        panel.contentView = background
        host.view.translatesAutoresizingMaskIntoConstraints = false
        background.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: background.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: background.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: background.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: background.bottomAnchor),
        ])
        panel.onCancel = { [weak self] in self?.hide() }
        // The file browser opens in front of a floating panel, and dragging it aside counts as a click elsewhere:
        // so the popup steps out of the way while the browser is open and comes back when it is closed.
        model.hidePopup = { [weak self] in self?.hide() }
        model.showPopup = { [weak self] in self?.show() }

        // The size comes from the content; the position never does after the popup opened.
        sizeObservation = host.observe(\.preferredContentSize) { [weak self] host, _ in
            MainActor.assumeIsolated { self?.contentSizeChanged(host.preferredContentSize) }
        }

        if let button = statusItem.button {
            button.target = self
            button.action = #selector(toggle)
            button.addSubview(IconDropView(frame: button.bounds) { [weak self] urls in
                self?.model.add(urls)
                self?.show()
            } click: { [weak self] in self?.toggle() })
        }
        // `paperplane` normally and `arrow.down.circle` while anything is being received: two shapes, not two colours.
        iconObservation = model.$receives.map { !$0.isEmpty }.removeDuplicates().sink { [weak self] receiving in
            let image = NSImage(systemSymbolName: receiving ? "arrow.down.circle" : "paperplane", accessibilityDescription: "Tailshare")
            self?.statusItem.button?.image = image
        }
    }

    private static func roundedMask(radius: CGFloat) -> NSImage {
        let side = radius * 2 + 1
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }

    @objc func toggle() {
        panel.isVisible ? hide() : show()
    }

    func show() {
        guard let button = statusItem.button, let iconWindow = button.window, !panel.isVisible else { return }
        let size = host.preferredContentSize == .zero ? host.view.fittingSize : host.preferredContentSize
        let screen = iconWindow.screen ?? NSScreen.main ?? NSScreen.screens[0]
        let icon = iconWindow.convertToScreen(button.convert(button.bounds, to: nil))
        panel.setFrame(PopupPlacement.frame(icon: icon, size: size, screen: screen.frame, visible: screen.visibleFrame), display: true)

        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        installMonitors()
        Task { await model.refreshDevices(force: true) }
    }

    func hide() {
        panel.orderOut(nil)
        removeMonitors()
    }

    private func contentSizeChanged(_ size: CGSize) {
        guard panel.isVisible, size != .zero else { return }
        panel.setFrame(PopupPlacement.resized(panel.frame, to: size), display: true)
    }

    // MARK: Closing

    // Closes on Escape (the panel), on a second click on the icon, and on a plain click anywhere else. A click that
    // turns into a drag (a file out of a Finder window) leaves it open, so the file can be dropped on it. Clicks in
    // the app's own windows, the file browser included, are not "elsewhere".
    private func installMonitors() {
        removeMonitors()
        let down = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.pendingOutsideClick = true
                self?.dragChangeCountAtMouseDown = NSPasteboard(name: .drag).changeCount
            }
        }
        let up = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseUp, .rightMouseUp]) { [weak self] _ in
            MainActor.assumeIsolated { self?.outsideClickEnded() }
        }
        monitors = [down, up].compactMap { $0 }
    }

    private func outsideClickEnded() {
        guard pendingOutsideClick else { return }
        pendingOutsideClick = false
        if NSPasteboard(name: .drag).changeCount == dragChangeCountAtMouseDown { hide() }
    }

    private func removeMonitors() {
        monitors.forEach { NSEvent.removeMonitor($0) }
        monitors = []
        pendingOutsideClick = false
    }
}

/// A borderless panel that can still take the keyboard (the text box), floats above ordinary windows but
/// below the file browser, and is shown over a full-screen app's space too.
private final class PopupPanel: NSPanel {
    var onCancel: (() -> Void)?

    init(contentRect: NSRect) {
        super.init(contentRect: contentRect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { true }

    override func cancelOperation(_ sender: Any?) { onCancel?() }
}

/// Covers the icon: files dropped on it are added to the popup, and a click still reaches the icon's action.
private final class IconDropView: NSView {
    private let onDrop: ([URL]) -> Void
    private let onClick: () -> Void

    init(frame: NSRect, onDrop: @escaping ([URL]) -> Void, click: @escaping () -> Void) {
        self.onDrop = onDrop
        onClick = click
        super.init(frame: frame)
        autoresizingMask = [.width, .height]
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) { fatalError("not from a nib") }

    override func mouseDown(with event: NSEvent) { onClick() }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { .copy }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let urls = sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        guard !urls.isEmpty else { return false }
        onDrop(urls)
        return true
    }
}
