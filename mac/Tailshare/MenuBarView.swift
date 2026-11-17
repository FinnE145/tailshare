import AppKit
import SwiftUI
import TailshareCore
import UniformTypeIdentifiers

struct MenuBarView: View {
    @ObservedObject var model: AppModel
    @State private var dropping = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            DeviceListView(model: model, selection: $model.selectedID)

            Picker("", selection: $model.mode) {
                Text("Files").tag(AppModel.Mode.files)
                Text("Text").tag(AppModel.Mode.text)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            switch model.mode {
            case .files: FilesPane(model: model, dropping: dropping)
            case .text: TextPane(model: model)
            }

            SendButtonRow(model: model)
            SendStatusView(send: model.send)
            ReceiveStatusView(model: model)
            PortNotice(state: model.nodeState)

            Divider()
            HStack {
                Spacer()
                Button("Quit Tailshare") { NSApplication.shared.terminate(nil) }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(16)
        .frame(width: 340)
        .onDrop(of: [.fileURL], isTargeted: $dropping) { providers in
            loadURLs(providers) { model.add($0) }
            return true
        }
    }
}

private struct FilesPane: View {
    @ObservedObject var model: AppModel
    let dropping: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if model.items.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "square.and.arrow.down").font(.title2).foregroundStyle(.secondary)
                    Text("Drop files or folders here").foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, minHeight: 84)
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                        .foregroundStyle(dropping ? Color.accentColor : Color.secondary.opacity(0.5))
                )
            } else {
                ScrollView {
                    VStack(spacing: 6) {
                        ForEach(model.items, id: \.self) { url in
                            ItemRow(name: url.lastPathComponent, symbol: symbol(for: url)) { model.items.removeAll { $0 == url } }
                        }
                    }
                    .padding(8)
                }
                .frame(height: CGFloat(min(model.items.count, 4)) * 26 + 10)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.08)))
            }

            HStack {
                Button("Browse") { choose(files: true, directories: false) }
                Button("Folder") { choose(files: false, directories: true) }
                Spacer()
                if !model.items.isEmpty { Button("Clear") { model.items = [] }.buttonStyle(.borderless).foregroundStyle(.secondary) }
            }
        }
    }

    private func choose(files: Bool, directories: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = files
        panel.canChooseDirectories = directories
        panel.allowsMultipleSelection = files
        model.hidePopup?()
        NSApp.activate(ignoringOtherApps: true)
        panel.begin { response in
            Task { @MainActor in
                if response == .OK { model.add(panel.urls) }
                model.showPopup?()
            }
        }
    }
}

private struct TextPane: View {
    @ObservedObject var model: AppModel

    var body: some View {
        TextEditor(text: $model.text)
            .font(.body)
            .scrollContentBackground(.hidden)
            .padding(6)
            .frame(height: 110)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(0.08)))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.secondary.opacity(0.3)))
    }
}

private struct SendButtonRow: View {
    @ObservedObject var model: AppModel

    var body: some View {
        Button {
            model.sendFromPopup()
        } label: {
            Label(model.selectedDevice.map { "Send to \($0.identity.name)" } ?? "Send", systemImage: "paperplane")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .disabled(!model.canSend)
    }
}

private struct ReceiveStatusView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ForEach(model.receives.filter(\.showsProgress)) { item in
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Label(item.text, systemImage: "arrow.down.circle")
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Cancel") { item.transfer.cancel() }.controlSize(.small)
                }
                ProgressView(value: item.total > 0 ? Double(item.bytes) / Double(item.total) : 0)
            }
            .font(.callout)
        }
    }
}

private struct PortNotice: View {
    let state: NodeState

    var body: some View {
        if case .portInUse(let port) = state {
            Label("Can't receive: port \(port) is in use", systemImage: "exclamationmark.triangle")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }
}

/// File URLs from a drop or a pasteboard; the provider callbacks arrive on background threads.
func loadURLs(_ providers: [NSItemProvider], completion: @escaping @MainActor ([URL]) -> Void) {
    let group = DispatchGroup()
    let lock = NSLock()
    var urls: [URL] = []
    for provider in providers where provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
        group.enter()
        provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
            defer { group.leave() }
            let url: URL?
            if let data = item as? Data { url = URL(dataRepresentation: data, relativeTo: nil) } else { url = item as? URL }
            if let url { lock.withLock { urls.append(url) } }
        }
    }
    group.notify(queue: .main) { Task { @MainActor in completion(urls) } }
}
