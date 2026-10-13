#if canImport(FoundationModels)
import Foundation
import FoundationModels

@Generable
struct GeneratedStoryDraft {
    @Guide(description: "A short, warm bedtime story title of at most eight words")
    var title: String

    @Guide(description: "Calm bedtime story scenes in reading order, one short paragraph each", .count(1...8))
    var scenes: [String]
}

/// Drafts story text with Apple's on-device foundation model (Apple Intelligence).
/// No model files are bundled or downloaded by the app; the OS owns the model.
public struct AppleIntelligenceStoryDrafter: Sendable {
    public init() {}

    public var availability: AppleIntelligenceAvailability {
        switch SystemLanguageModel.default.availability {
        case .available:
            return .available
        case .unavailable(.deviceNotEligible):
            return .deviceNotEligible
        case .unavailable(.appleIntelligenceNotEnabled):
            return .appleIntelligenceNotEnabled
        case .unavailable(.modelNotReady):
            return .modelNotReady
        case .unavailable:
            return .unavailable
        }
    }

    public func draft(_ input: DraftStoryInput) async throws -> StoryDraftReply? {
        let session = LanguageModelSession(instructions: StoryPromptBuilder.instructions)
        let response = try await session.respond(
            to: StoryPromptBuilder.prompt(for: input),
            generating: GeneratedStoryDraft.self
        )
        return StoryPromptBuilder.normalized(title: response.content.title, scenes: response.content.scenes)
    }
}
#endif
