import AppKit
import TailshareCore
import UserNotifications

/// Every notification the app raises. Titles and bodies are built by Core; this only shows them and
/// handles the clicks.
final class Notifier: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    private enum Category {
        static let file = "received-file"
        static let text = "received-text"
        static let link = "received-link"
        static let copyAction = "copy"
    }

    private let center = UNUserNotificationCenter.current()
    private let lock = NSLock()
    private var texts: [String: String] = [:]
    private var textOrder: [String] = []
    /// Received texts kept for their notification's click; the oldest go first so a long session can't pile up megabytes.
    private let textsKept = 20

    func setUp() {
        center.delegate = self
        let copy = UNNotificationAction(identifier: Category.copyAction, title: "Copy", options: [])
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Category.file, actions: [], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.text, actions: [], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.link, actions: [copy], intentIdentifiers: []),
        ])
        center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func post(_ notice: Notice) { add(notice, category: nil, userInfo: [:]) }

    /// The notification a finished send ends in; a cancelled one raises none.
    func postSendResult(_ outcome: SendOutcome, device: String) {
        switch outcome {
        case .sent: post(Notices.sent(to: device))
        case .declined: post(Notices.declined(by: device))
        case .didntRespond: post(Notices.noResponse(from: device))
        case .failed: post(Notices.failed(to: device))
        case .pickedUp: post(Notices.pickedUp(by: device))
        case .notPickedUp: post(Notices.notPickedUp(by: device))
        case .cancelled: break
        }
    }

    func postReceivedFiles(_ notice: Notice, urls: [URL]) {
        // One top-level item is revealed, several open the receive folder: both need only the paths.
        add(notice, category: Category.file, userInfo: ["paths": urls.map(\.path)])
    }

    func postReceivedText(device: String, text: String) {
        let notice = Notices.receivedText(device: device, text: text)
        let id = UUID().uuidString
        // Held here, not in the notification: the full text can be a megabyte and a click copies all of it.
        lock.withLock {
            texts[id] = text
            textOrder.append(id)
            while textOrder.count > textsKept { texts[textOrder.removeFirst()] = nil }
        }
        let isLink = LinkDetector.link(in: text) != nil
        add(notice, category: isLink ? Category.link : Category.text, userInfo: ["textID": id])
    }

    private func add(_ notice: Notice, category: String?, userInfo: [AnyHashable: Any]) {
        let content = UNMutableNotificationContent()
        content.title = notice.title
        content.body = notice.body
        content.sound = .default
        if let category { content.categoryIdentifier = category }
        content.userInfo = userInfo
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }

    // MARK: Delegate

    // The app is a menu-bar app that is "in front" whenever its popup is open: show banners then too.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let info = response.notification.request.content.userInfo
        let isCopy = response.actionIdentifier == Category.copyAction
        let isDefault = response.actionIdentifier == UNNotificationDefaultActionIdentifier
        guard isCopy || isDefault else { return }

        let paths = info["paths"] as? [String]
        let textID = info["textID"] as? String
        await MainActor.run {
            if let paths {
                reveal(paths.map { URL(fileURLWithPath: $0) })
            } else if let id = textID, let text = lock.withLock({ texts[id] }) {
                // Only an http(s) link is ever opened, and only when the notification itself is clicked.
                if isDefault, let url = LinkDetector.link(in: text) {
                    NSWorkspace.shared.open(url)
                } else {
                    copy(text)
                }
            }
        }
    }

    @MainActor
    private func reveal(_ urls: [URL]) {
        if urls.count == 1 {
            NSWorkspace.shared.activateFileViewerSelecting(urls)
        } else if let folder = urls.first?.deletingLastPathComponent() {
            NSWorkspace.shared.open(folder)
        }
    }

    @MainActor
    private func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}
