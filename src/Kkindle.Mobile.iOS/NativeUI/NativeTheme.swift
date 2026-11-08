import SwiftUI
import UIKit

/// The same type scale, ink and spacing as MobileTheme.axaml. Native controls
/// own their interaction; their default visual style isn't the app's design.
enum NativeTheme {
    static func font(_ size: CGFloat = 15, relativeTo style: Font.TextStyle = .body) -> Font {
        .custom("KingHwaOldSong", size: size, relativeTo: style)
    }

    private static func color(_ light: UInt32, _ dark: UInt32) -> Color {
        Color(uiColor: UIColor { traits in
            let value = traits.userInterfaceStyle == .dark ? dark : light
            return UIColor(red: CGFloat((value >> 16) & 0xff) / 255,
                           green: CGFloat((value >> 8) & 0xff) / 255,
                           blue: CGFloat(value & 0xff) / 255, alpha: 1)
        })
    }

    static let page = color(0xffffff, 0x121212)
    static let surface = color(0xffffff, 0x1e1e1e)
    static let sunk = color(0xf5f5f5, 0x2a2a2a)
    static let strong = color(0xe6e6e6, 0x3a3a3a)
    static let ink = color(0x1c1c1c, 0xededed)
    static let secondary = color(0x5e5e5e, 0xb0b0b0)
    static let muted = color(0x767676, 0x8c8c8c)
    static let faint = color(0x9a9a9a, 0x6e6e6e)
    static let divider = color(0xefefef, 0x262626)

    @MainActor static func configureControls() {
        func uiFont(_ size: CGFloat) -> UIFont { UIFont(name: "KingHwaOldSong", size: size) ?? .systemFont(ofSize: size) }
        let navigation = UINavigationBarAppearance()
        navigation.configureWithTransparentBackground()
        navigation.titleTextAttributes = [.font: uiFont(17), .foregroundColor: UIColor.label]
        navigation.largeTitleTextAttributes = [.font: uiFont(26), .foregroundColor: UIColor.label]
        UINavigationBar.appearance().standardAppearance = navigation
        UINavigationBar.appearance().scrollEdgeAppearance = navigation
        UINavigationBar.appearance().compactAppearance = navigation
        UIBarButtonItem.appearance().setTitleTextAttributes([.font: uiFont(15)], for: .normal)
        UIBarButtonItem.appearance().setTitleTextAttributes([.font: uiFont(15)], for: .disabled)
        UITabBarItem.appearance().setTitleTextAttributes([.font: uiFont(10)], for: .normal)
        UITabBarItem.appearance().setTitleTextAttributes([.font: uiFont(10)], for: .selected)
        UITabBar.appearance().tintColor = .label
        UITabBar.appearance().unselectedItemTintColor = .secondaryLabel
    }
}

struct NativePrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(NativeTheme.font()).foregroundStyle(enabled ? NativeTheme.ink : NativeTheme.muted)
            .frame(maxWidth: .infinity).padding(.horizontal, 16).padding(.vertical, 14)
            .background(configuration.isPressed ? NativeTheme.strong : NativeTheme.sunk,
                        in: RoundedRectangle(cornerRadius: 12))
    }
}

struct NativeSettingsRow: View {
    let title: String
    var value = ""
    var chevron = true
    var body: some View {
        HStack(spacing: 8) {
            Text(title).font(NativeTheme.font())
            Spacer(minLength: 12)
            if !value.isEmpty { Text(value).font(NativeTheme.font(14)).foregroundStyle(NativeTheme.muted).lineLimit(1) }
            if chevron { Image(systemName: "chevron.right").font(.system(size: 12, weight: .regular)).foregroundStyle(NativeTheme.muted) }
        }.frame(minHeight: 22).padding(.vertical, 13).contentShape(Rectangle())
    }
}

struct NativeSearchField: View {
    @Binding var text: String
    let prompt: String
    let focused: FocusState<Bool>.Binding
    let identifier: String
    let submit: () -> Void
    var body: some View {
        TextField(prompt, text: $text).font(NativeTheme.font(15)).focused(focused)
            .textInputAutocapitalization(.never).disableAutocorrection(true).submitLabel(.search)
            .frame(minHeight: 44).accessibilityIdentifier(identifier).onSubmit(submit)
    }
}

struct NativeTextField: View {
    let title: String
    @Binding var text: String
    var secure = false
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(NativeTheme.font(13, relativeTo: .caption)).foregroundStyle(NativeTheme.muted)
            Group {
                if secure { SecureField(title, text: $text) }
                else { TextField(title, text: $text) }
            }.font(NativeTheme.font()).padding(.horizontal, 12).padding(.vertical, 11)
                .background(NativeTheme.sunk, in: RoundedRectangle(cornerRadius: 12))
                .accessibilityLabel(title)
        }
    }
}

struct NativeToggle: View {
    let title: String
    @Binding var isOn: Bool
    var identifier = ""
    var body: some View {
        HStack {
            Text(title).font(NativeTheme.font()).accessibilityHidden(true)
            Spacer(minLength: 12)
            NativeSwitch(title: title, isOn: $isOn, identifier: identifier).fixedSize()
        }.frame(minHeight: 44).accessibilityElement(children: .contain)
    }
}

/// Keep UIKit's switch bounds and accessibility element together. SwiftUI can
/// expand a Toggle's accessibility frame to its List row after scrolling.
private struct NativeSwitch: UIViewRepresentable {
    let title: String
    @Binding var isOn: Bool
    let identifier: String
    @Environment(\.isEnabled) private var enabled

    func makeCoordinator() -> Coordinator { Coordinator(isOn: $isOn) }

    func makeUIView(context: Context) -> UISwitch {
        let control = UISwitch()
        control.setContentHuggingPriority(.required, for: .horizontal)
        control.setContentHuggingPriority(.required, for: .vertical)
        control.setContentCompressionResistancePriority(.required, for: .horizontal)
        control.setContentCompressionResistancePriority(.required, for: .vertical)
        control.addTarget(context.coordinator, action: #selector(Coordinator.valueChanged(_:)), for: .valueChanged)
        return control
    }

    func updateUIView(_ control: UISwitch, context: Context) {
        context.coordinator.isOn = $isOn
        control.isEnabled = enabled
        control.accessibilityLabel = title
        control.accessibilityIdentifier = identifier
        if control.isOn != isOn { control.setOn(isOn, animated: false) }
    }

    @available(iOS 16.0, *)
    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UISwitch, context: Context) -> CGSize? {
        uiView.intrinsicContentSize
    }

    final class Coordinator: NSObject {
        var isOn: Binding<Bool>
        init(isOn: Binding<Bool>) { self.isOn = isOn }
        @objc func valueChanged(_ control: UISwitch) { isOn.wrappedValue = control.isOn }
    }
}

struct NativeThinProgress: View {
    let value: Double
    var total = 100.0
    var height: CGFloat = 2
    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                NativeTheme.strong
                NativeTheme.ink.frame(width: geometry.size.width * min(1, max(0, value / max(1, total))))
            }.clipShape(Capsule())
        }.frame(height: height).accessibilityHidden(true)
    }
}

/// Plain native rows keep the keyboard, scrolling and accessibility behavior
/// of List without introducing the gray grouped Settings-app appearance.
struct NativeForm<Content: View>: View {
    @ViewBuilder let content: Content
    var body: some View {
        List {
            content
                .listRowInsets(EdgeInsets(top: 13, leading: 24, bottom: 13, trailing: 24))
                .listRowSeparator(.hidden).listRowBackground(NativeTheme.page)
        }.listStyle(.plain).textCase(nil).font(NativeTheme.font()).buttonStyle(.borderless)
            .modifier(NativeListBackground())
    }
}

struct NativeListBackground: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.scrollContentBackground(.hidden).background(NativeTheme.page) }
        else { content.background(NativeTheme.page) }
    }
}

struct NativeFloatingSurface: ViewModifier {
    @Environment(\.colorScheme) private var scheme
    func body(content: Content) -> some View {
        content.background(NativeTheme.surface.opacity(0.97), in: RoundedRectangle(cornerRadius: 16))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(NativeTheme.divider, lineWidth: 0.5))
            .shadow(color: .black.opacity(scheme == .dark ? 0.25 : 0.10), radius: 10, y: 4)
    }
}

struct NativeHideTabBar: ViewModifier {
    @ViewBuilder func body(content: Content) -> some View {
        if #available(iOS 16.0, *) { content.toolbar(.hidden, for: .tabBar) }
        else { content }
    }
}
