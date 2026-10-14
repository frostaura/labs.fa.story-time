import Foundation
import FoundationModels

/// Error codes crossing the bridge. Values are part of the C# contract
/// (StoryTime.Core FmErrorCode) and must never be renumbered.
@objc(SEFMErrorCode) public enum SEFMErrorCode: Int, Sendable {
    case ok = 0
    case unavailableDevice = 1
    case unavailableNotEnabled = 2
    case unavailableModelNotReady = 3
    case unsupportedLocale = 4
    case contextExceeded = 10
    case guardrail = 11
    case refusal = 12
    case rateLimited = 13
    case concurrent = 14
    case timeout = 15
    case parse = 16
    case assetsUnavailable = 17
    case cancelled = 20
    case unknown = 99
}

/// Sampling and length options sent with every request (JSON `options`).
struct SEFMOptions: Decodable, Sendable {
    /// "greedy", "topK" or "threshold" (probability threshold). Absent → model default.
    var sampling: String?
    var topK: Int?
    var probabilityThreshold: Double?
    var seed: UInt64?
    var temperature: Double?
    var maximumResponseTokens: Int?

    var generationOptions: GenerationOptions {
        let mode: GenerationOptions.SamplingMode?
        switch sampling {
        case "greedy":
            mode = .greedy
        case "topK":
            mode = .random(top: max(1, topK ?? 20), seed: seed)
        case "threshold":
            mode = .random(probabilityThreshold: probabilityThreshold ?? 0.9, seed: seed)
        default:
            mode = nil
        }
        return GenerationOptions(samplingMode: mode, temperature: temperature, maximumResponseTokens: maximumResponseTokens)
    }
}

/// One stage request: instructions open a fresh session, prompt is the single turn.
struct SEFMRequest: Decodable, Sendable {
    var instructions: String
    var prompt: String
    var options: SEFMOptions?
    /// Outline only: exact number of scenes the guided schema must produce.
    var sceneCount: Int?
    /// "default" (absent) or "permissive". Permissive is a ladder step for plain-String
    /// stages only (lab 2026-10-14: default guardrails tripped input-side on benign scene
    /// prompts; the same prompt passed under `.permissiveContentTransformations`). The C#
    /// side ALWAYS runs its lexicon filter on that output.
    var guardrails: String?

    var model: SystemLanguageModel {
        guardrails == "permissive"
            ? SystemLanguageModel(useCase: .general, guardrails: .permissiveContentTransformations)
            : SystemLanguageModel.default
    }

    static func decode(_ json: String) throws -> SEFMRequest {
        try JSONDecoder().decode(SEFMRequest.self, from: Data(json.utf8))
    }
}

/// Plain-text result envelope (scene, polish).
struct SEFMTextResult: Encodable {
    var text: String
}

enum SEFMJSON {
    static func encode<T: Encodable>(_ value: T) -> String {
        guard let data = try? JSONEncoder().encode(value), let s = String(data: data, encoding: .utf8) else { return "{}" }
        return s
    }

    static func object(_ dict: [String: Any]) -> String {
        guard JSONSerialization.isValidJSONObject(dict),
              let data = try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys]),
              let s = String(data: data, encoding: .utf8) else { return "{}" }
        return s
    }
}
