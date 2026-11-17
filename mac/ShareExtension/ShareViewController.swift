import Cocoa
import UniformTypeIdentifiers

/// No UI of its own: collects what was shared, hands it to the app with a tailshare: link, and
/// finishes at once. No network and no Tailscale here; the extension is sandboxed and stays dumb.
final class ShareViewController: NSViewController {
    private static let scheme = "tailshare"
    private static let host = "share"
    private static let pasteboardName = "io.github.finne145.tailshare.handoff"

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: 1, height: 1))
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        Task { await handOff() }
    }

    private func handOff() async {
        let providers = (extensionContext?.inputItems as? [NSExtensionItem] ?? []).flatMap { $0.attachments ?? [] }

        var paths: [String] = []
        var text: String?
        for provider in providers {
            if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier),
               let url = await loadURL(provider, UTType.fileURL) {
                paths.append(url.path)
            } else if text == nil, provider.hasItemConformingToTypeIdentifier(UTType.url.identifier),
                      let url = await loadURL(provider, UTType.url) {
                // A shared web link is text, and the receiver's link rule makes it clickable.
                text = url.absoluteString
            } else if text == nil, provider.hasItemConformingToTypeIdentifier(UTType.plainText.identifier),
                      let string = await loadString(provider) {
                text = string
            }
        }

        if !paths.isEmpty {
            var c = URLComponents()
            c.scheme = Self.scheme
            c.host = Self.host
            c.queryItems = paths.map { URLQueryItem(name: "file", value: $0) }
            if let url = c.url { NSWorkspace.shared.open(url) }
        } else if let text, let url = URL(string: "\(Self.scheme)://\(Self.host)?text=1") {
            let pasteboard = NSPasteboard(name: NSPasteboard.Name(Self.pasteboardName))
            pasteboard.clearContents()
            pasteboard.setString(text, forType: .string)
            NSWorkspace.shared.open(url)
        }

        // The app checks which process sent the link; this one has to still be alive when it looks.
        try? await Task.sleep(nanoseconds: 500_000_000)
        extensionContext?.completeRequest(returningItems: nil)
    }

    private func loadURL(_ provider: NSItemProvider, _ type: UTType) async -> URL? {
        await withCheckedContinuation { c in
            provider.loadItem(forTypeIdentifier: type.identifier, options: nil) { item, _ in
                if let url = item as? URL { c.resume(returning: url) }
                else if let data = item as? Data { c.resume(returning: URL(dataRepresentation: data, relativeTo: nil)) }
                else { c.resume(returning: nil) }
            }
        }
    }

    private func loadString(_ provider: NSItemProvider) async -> String? {
        await withCheckedContinuation { c in
            provider.loadItem(forTypeIdentifier: UTType.plainText.identifier, options: nil) { item, _ in
                if let s = item as? String { c.resume(returning: s) }
                else if let data = item as? Data { c.resume(returning: String(data: data, encoding: .utf8)) }
                else { c.resume(returning: nil) }
            }
        }
    }
}
