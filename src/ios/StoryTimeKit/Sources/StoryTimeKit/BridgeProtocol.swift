import Foundation

/// Wire contract shared with `src/frontend/src/services/nativeBridge.ts`.
public enum BridgeProtocol {
    public static let handlerName = "storyTime"
    public static let dictationEventName = "storytime:dictation"
    public static let appleIntelligenceProvider = "apple-foundation-models"
}

public enum BridgeAction: String, Sendable {
    case capabilities
    case draftStory
    case startDictation
    case stopDictation
    case narrateStory
    case loadNarration
}

/// Mirrors the TypeScript `AppleIntelligenceAvailability` union.
public enum AppleIntelligenceAvailability: String, Sendable {
    case available
    case deviceNotEligible
    case appleIntelligenceNotEnabled
    case modelNotReady
    case unavailable
}

public struct NativeCapabilities: Equatable, Sendable {
    public var appleIntelligence: AppleIntelligenceAvailability
    public var dictation: Bool
    public var narration: Bool

    public init(appleIntelligence: AppleIntelligenceAvailability, dictation: Bool, narration: Bool = false) {
        self.appleIntelligence = appleIntelligence
        self.dictation = dictation
        self.narration = narration
    }

    public var replyBody: [String: Any] {
        ["appleIntelligence": appleIntelligence.rawValue, "dictation": dictation, "narration": narration]
    }
}

public struct StoryBibleInput: Decodable, Equatable, Sendable {
    public var arcName: String?
    public var arcEpisodeNumber: Int?
    public var arcObjective: String?
    public var previousEpisodeSummary: String?
    public var recurringCharacter: String?
    public var continuityFacts: [String]?
}

public struct DraftStoryInput: Decodable, Equatable, Sendable {
    public var childName: String
    public var mode: String
    public var durationMinutes: Int
    public var customization: [String: String]?
    public var storyBible: StoryBibleInput?

    public init(
        childName: String,
        mode: String,
        durationMinutes: Int,
        customization: [String: String]? = nil,
        storyBible: StoryBibleInput? = nil
    ) {
        self.childName = childName
        self.mode = mode
        self.durationMinutes = durationMinutes
        self.customization = customization
        self.storyBible = storyBible
    }
}

public struct StoryDraftReply: Equatable, Sendable {
    public var title: String
    public var scenes: [String]

    public init(title: String, scenes: [String]) {
        self.title = title
        self.scenes = scenes
    }

    public var replyBody: [String: Any] {
        ["title": title, "scenes": scenes]
    }
}

public struct NarrateStoryInput: Decodable, Equatable, Sendable {
    public var storyId: String
    public var title: String
    public var recap: String?
    public var scenes: [String]
    public var narrationStyle: String?

    public init(storyId: String, title: String, recap: String? = nil, scenes: [String], narrationStyle: String? = nil) {
        self.storyId = storyId
        self.title = title
        self.recap = recap
        self.scenes = scenes
        self.narrationStyle = narrationStyle
    }
}

public enum NarrationPart: String, Sendable, CaseIterable {
    case teaser
    case full
}

public enum BridgeRequestError: Error, Equatable {
    case malformedMessage
    case unknownAction(String)
    case missingInput
    case invalidStoryId
}

public enum BridgeRequest: Equatable, Sendable {
    case capabilities
    case draftStory(DraftStoryInput)
    case startDictation
    case stopDictation
    case narrateStory(NarrateStoryInput)
    case loadNarration(storyId: String, part: NarrationPart)

    /// Parses the `postMessage` body delivered by WebKit (a property-list style dictionary).
    public static func parse(_ body: Any) throws -> BridgeRequest {
        guard let message = body as? [String: Any], let rawAction = message["action"] as? String else {
            throw BridgeRequestError.malformedMessage
        }
        guard let action = BridgeAction(rawValue: rawAction) else {
            throw BridgeRequestError.unknownAction(rawAction)
        }

        switch action {
        case .capabilities:
            return .capabilities
        case .startDictation:
            return .startDictation
        case .stopDictation:
            return .stopDictation
        case .draftStory:
            return .draftStory(try decodeInput(DraftStoryInput.self, from: message))
        case .narrateStory:
            let input = try decodeInput(NarrateStoryInput.self, from: message)
            guard NarrationScript.isValidStoryId(input.storyId) else {
                throw BridgeRequestError.invalidStoryId
            }
            return .narrateStory(input)
        case .loadNarration:
            guard let storyId = message["storyId"] as? String,
                  let rawPart = message["part"] as? String,
                  let part = NarrationPart(rawValue: rawPart) else {
                throw BridgeRequestError.missingInput
            }
            guard NarrationScript.isValidStoryId(storyId) else {
                throw BridgeRequestError.invalidStoryId
            }
            return .loadNarration(storyId: storyId, part: part)
        }
    }

    private static func decodeInput<T: Decodable>(_ type: T.Type, from message: [String: Any]) throws -> T {
        guard let input = message["input"], JSONSerialization.isValidJSONObject(input) else {
            throw BridgeRequestError.missingInput
        }
        let data = try JSONSerialization.data(withJSONObject: input)
        return try JSONDecoder().decode(type, from: data)
    }
}

/// Builds the JavaScript that relays live dictation results to the web app.
public enum DictationEventScript {
    public static func make(text: String, isFinal: Bool) -> String {
        let detail: [String: Any] = ["text": text, "isFinal": isFinal]
        let data = (try? JSONSerialization.data(withJSONObject: detail)) ?? Data("{}".utf8)
        let json = String(decoding: data, as: UTF8.self)
        return "window.dispatchEvent(new CustomEvent('\(BridgeProtocol.dictationEventName)', { detail: \(json) }));"
    }
}
