import StoryTimeKit
import WebKit

/// Answers bridge calls from the web app with Apple Intelligence drafting, on-device dictation,
/// and on-device narration (Apple speech synthesis).
@MainActor
final class NativeBridgeHandler: NSObject, WKScriptMessageHandlerWithReply {
    weak var webView: WKWebView?

    private let drafter = AppleIntelligenceStoryDrafter()
    private let dictation = OnDeviceDictation()
    private let narrator = OnDeviceNarrator()

    func userContentController(
        _ userContentController: WKUserContentController,
        didReceive message: WKScriptMessage
    ) async -> (Any?, String?) {
        do {
            switch try BridgeRequest.parse(message.body) {
            case .capabilities:
                let capabilities = NativeCapabilities(
                    appleIntelligence: drafter.availability,
                    dictation: await OnDeviceDictation.isSupported,
                    narration: OnDeviceNarrator.isSupported
                )
                return (capabilities.replyBody, nil)
            case let .draftStory(input):
                guard drafter.availability == .available else {
                    return (NSNull(), nil)
                }
                let draft = try await drafter.draft(input)
                return (draft?.replyBody ?? NSNull(), nil)
            case .startDictation:
                let transcript = try await dictation.start { [weak self] text, isFinal in
                    self?.relayDictation(text: text, isFinal: isFinal)
                }
                return (["transcript": transcript], nil)
            case .stopDictation:
                dictation.stop()
                return (NSNull(), nil)
            case let .narrateStory(input):
                let result = try await narrator.narrate(input)
                return (result.replyBody, nil)
            case let .loadNarration(storyId, part):
                guard let dataUri = narrator.dataUri(storyId: storyId, part: part) else {
                    return (NSNull(), nil)
                }
                return (["dataUri": dataUri], nil)
            }
        } catch {
            return (nil, String(describing: error))
        }
    }

    func shutdown() {
        dictation.stop()
    }

    private func relayDictation(text: String, isFinal: Bool) {
        webView?.evaluateJavaScript(DictationEventScript.make(text: text, isFinal: isFinal))
    }
}
