import Foundation
import FoundationModels

/// Maps every error the framework can throw onto the bridge's error codes. StoryTime's floor
/// is iOS 27 (founder decision 2026-10-14), so only the iOS 27 families are mapped:
/// `LanguageModelError`, `SystemLanguageModel.Error`, `LanguageModelSession.Error` and
/// `GeneratedContent.ParsingError` (the iOS 26 `GenerationError` is deprecated there).
/// Error payload JSON: `{"message": String, "explanation": String?, "resetDate": Double? (unix seconds)}`.
enum SEFMErrorMapper {
    static func map(_ error: any Error) async -> (SEFMErrorCode, String) {
        if error is CancellationError {
            return (.cancelled, payload("cancelled"))
        }
        if let e = error as? LanguageModelError {
            switch e {
            case .contextSizeExceeded(let info):
                return (.contextExceeded, payload("context size \(info.contextSize) exceeded (\(info.tokenCount) tokens)"))
            case .rateLimited(let info):
                return (.rateLimited, payload(info.debugDescription, resetDate: info.resetDate))
            case .guardrailViolation(let info):
                return (.guardrail, payload(info.debugDescription))
            case .refusal(let info):
                let explanation = try? await info.explanation.content
                return (.refusal, payload(info.debugDescription, explanation: explanation))
            case .unsupportedLanguageOrLocale(let info):
                return (.unsupportedLocale, payload(info.debugDescription))
            case .timeout(let info):
                return (.timeout, payload(info.debugDescription))
            case .unsupportedCapability(let info):
                return (.unknown, payload(info.debugDescription))
            case .unsupportedTranscriptContent(let info):
                return (.unknown, payload(info.debugDescription))
            case .unsupportedGenerationGuide(let info):
                return (.parse, payload(info.debugDescription))
            @unknown default:
                return (.unknown, payload(String(describing: e)))
            }
        }
        if let e = error as? SystemLanguageModel.Error {
            switch e {
            case .assetsUnavailable(let info):
                return (.assetsUnavailable, payload(info.debugDescription))
            @unknown default:
                return (.assetsUnavailable, payload(String(describing: e)))
            }
        }
        if error is LanguageModelSession.Error {
            return (.concurrent, payload(String(describing: error)))
        }
        if let e = error as? GeneratedContent.ParsingError {
            return (.parse, payload(e.debugDescription))
        }
        if error is DecodingError {
            return (.parse, payload("request JSON could not be decoded: \(error.localizedDescription)"))
        }
        return (.unknown, payload(String(describing: error)))
    }

    static func payload(_ message: String, explanation: String? = nil, resetDate: Date? = nil) -> String {
        var dict: [String: Any] = ["message": message]
        if let explanation { dict["explanation"] = explanation }
        if let resetDate { dict["resetDate"] = resetDate.timeIntervalSince1970 }
        return SEFMJSON.object(dict)
    }
}
