import Foundation
import TailshareCore

enum AppPaths {
    /// `~/Library/Application Support/tailshare/settings.json`, in A's shape.
    static var settingsStore: SettingsStore {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return SettingsStore(fileURL: base.appendingPathComponent("tailshare/settings.json"))
    }

    /// The private pasteboard the Share Extension leaves shared text on.
    static let handOffPasteboard = "io.github.finne145.tailshare.handoff"
}
