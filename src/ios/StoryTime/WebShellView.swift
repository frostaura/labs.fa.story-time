import StoryTimeKit
import SwiftUI
import WebKit

/// Hosts the StoryTime web app and exposes the native bridge (`window.webkit.messageHandlers.storyTime`).
struct WebShellView: UIViewRepresentable {
    let configuration: HostConfiguration

    func makeCoordinator() -> NativeBridgeHandler {
        NativeBridgeHandler()
    }

    func makeUIView(context: Context) -> WKWebView {
        let contentController = WKUserContentController()
        contentController.addScriptMessageHandler(
            context.coordinator,
            contentWorld: .page,
            name: BridgeProtocol.handlerName
        )

        if let apiBaseUrl = configuration.apiBaseUrl,
           let encoded = try? JSONSerialization.data(withJSONObject: [apiBaseUrl]),
           let literal = String(data: encoded, encoding: .utf8) {
            let script = "window.__STORYTIME_API_BASE_URL__ = \(literal)[0];"
            contentController.addUserScript(
                WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true)
            )
        }

        let webConfiguration = WKWebViewConfiguration()
        webConfiguration.userContentController = contentController
        webConfiguration.allowsInlineMediaPlayback = true
        webConfiguration.mediaTypesRequiringUserActionForPlayback = []

        let webView = WKWebView(frame: .zero, configuration: webConfiguration)
        webView.isInspectable = true
        webView.allowsBackForwardNavigationGestures = false
        context.coordinator.webView = webView
        webView.load(URLRequest(url: configuration.webAppUrl))
        return webView
    }

    func updateUIView(_ webView: WKWebView, context: Context) {}

    static func dismantleUIView(_ webView: WKWebView, coordinator: NativeBridgeHandler) {
        coordinator.shutdown()
        webView.configuration.userContentController.removeAllScriptMessageHandlers()
    }
}
