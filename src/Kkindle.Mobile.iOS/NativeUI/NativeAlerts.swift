import UIKit

/// System alerts are presented from the current native controller, including
/// an active form/sheet. Their lifetime never depends on Avalonia overlays.
@MainActor
enum NativeAlerts {
    private struct Pending {
        let id: Int64
        let title: String
        let message: String
        let confirm: String
        let cancellable: Bool
        let completion: (Bool) -> Void
    }
    private static var queue: [Pending] = []
    private static var active: Pending?
    private static var controller: UIAlertController?
    private static var errorId: Int64 = -1
    private static var retry: Task<Void, Never>?

    static func confirm(_ prompt: NativePrompt, store: NativeStore) {
        queue.append(Pending(id: prompt.id, title: prompt.title, message: prompt.message, confirm: prompt.confirm,
            cancellable: true, completion: { [weak store] accepted in store?.answerPrompt(prompt, accepted: accepted) }))
        presentNext()
    }
    static func error(_ text: String, dismissed: @escaping () -> Void) {
        errorId -= 1
        queue.append(Pending(id: errorId, title: "操作失败", message: text, confirm: "好", cancellable: false, completion: { _ in dismissed() }))
        presentNext()
    }
    static func dismissPrompt(_ id: Int64) {
        queue.removeAll { $0.id == id }
        guard active?.id == id else { return }
        active = nil
        let previous = controller; controller = nil
        previous?.dismiss(animated: true) { presentNext() }
    }
    private static func presentNext() {
        guard active == nil, !queue.isEmpty else { return }
        guard let presenter = topController(), !(presenter is UIAlertController), !presenter.isBeingDismissed else {
            scheduleNext(); return
        }
        let value = queue.removeFirst(); active = value
        let alert = UIAlertController(title: value.title, message: value.message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: value.confirm, style: .default) { _ in finish(value, accepted: true) })
        if value.cancellable { alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in finish(value, accepted: false) }) }
        controller = alert
        presenter.present(alert, animated: true)
    }
    private static func finish(_ value: Pending, accepted: Bool) {
        guard active?.id == value.id else { return }
        active = nil; controller = nil; value.completion(accepted); scheduleNext()
    }
    private static func scheduleNext() {
        retry?.cancel()
        retry = Task {
            do { try await Task.sleep(nanoseconds: 250_000_000) } catch { return }
            presentNext()
        }
    }
    private static func topController() -> UIViewController? {
        let windows = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows)
        guard var view = (windows.first { $0.isKeyWindow } ?? windows.first { !$0.isHidden })?.rootViewController else { return nil }
        while true {
            if let presented = view.presentedViewController { view = presented }
            else if let navigation = view as? UINavigationController, let visible = navigation.visibleViewController { view = visible }
            else if let tabs = view as? UITabBarController, let selected = tabs.selectedViewController { view = selected }
            else { return view }
        }
    }
}
