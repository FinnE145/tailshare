import SwiftUI
import TailshareCore

/// The list of devices to send to, shared by the popup and the send window.
struct DeviceListView: View {
    @ObservedObject var model: AppModel
    @Binding var selection: String?

    private static let rowHeight: CGFloat = 30
    private static let rowGap: CGFloat = 2

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Send to").font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Spacer()
                Button {
                    Task { await model.refreshDevices(force: true) }
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise").labelStyle(.iconOnly)
                }
                .buttonStyle(.borderless)
                .help("Refresh")
            }

            switch model.deviceState {
            case .loading:
                placeholder("Looking for devices", symbol: "magnifyingglass")
            case .none:
                placeholder("No devices", symbol: "desktopcomputer.trianglebadge.exclamationmark")
            case .tailscaleDown:
                placeholder("Tailscale not connected", symbol: "wifi.slash")
            case .list:
                // Sized by row count, so a short list isn't squeezed and a long one never ends on half a row.
                ScrollView {
                    VStack(spacing: Self.rowGap) {
                        ForEach(model.devices, id: \.identity.id) { peer in row(peer) }
                    }
                }
                .frame(height: CGFloat(min(model.devices.count, 5)) * (Self.rowHeight + Self.rowGap))
            }
        }
    }

    private func row(_ peer: PeerInfo) -> some View {
        let selected = selection == peer.identity.id
        return HStack(spacing: 8) {
            Image(systemName: "desktopcomputer").frame(width: 20).foregroundStyle(.secondary)
            Text(peer.identity.name).lineLimit(1)
            Spacer()
            // The checkmark carries the selection, the tint only backs it up.
            if selected { Image(systemName: "checkmark").fontWeight(.semibold) }
        }
        .padding(.horizontal, 8)
        .frame(height: Self.rowHeight)
        .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Color.accentColor.opacity(0.18) : Color.clear))
        .contentShape(Rectangle())
        .onTapGesture { selection = peer.identity.id }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }

    private func placeholder(_ text: String, symbol: String) -> some View {
        Label(text, systemImage: symbol)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, minHeight: 56)
    }
}

/// A send's status line, progress bar and Cancel.
struct SendStatusView: View {
    @ObservedObject var send: SendController

    var body: some View {
        if let line = send.status.line {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Label(line.text, systemImage: line.symbol).lineLimit(1)
                    Spacer()
                    if send.isBusy { Button("Cancel") { send.cancel() }.controlSize(.small) }
                }
                if let bar = send.status.fraction {
                    if let value = bar { ProgressView(value: value) } else { ProgressView().progressViewStyle(.linear) }
                }
            }
            .font(.callout)
        }
    }
}

struct ItemRow: View {
    let name: String
    let symbol: String
    var remove: (() -> Void)?

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: symbol).frame(width: 20).foregroundStyle(.secondary)
            Text(name).lineLimit(1).truncationMode(.middle)
            Spacer()
            if let remove {
                Button(action: remove) { Label("Remove", systemImage: "xmark.circle.fill").labelStyle(.iconOnly) }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Remove")
            }
        }
    }
}

func symbol(for url: URL) -> String {
    var isDir: ObjCBool = false
    FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)
    return isDir.boolValue ? "folder" : "doc"
}
