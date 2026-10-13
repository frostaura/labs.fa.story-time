import Foundation
import Testing
@testable import StoryTimeKit

@Suite("BridgeRequest")
struct BridgeRequestTests {
    @Test func parsesSimpleActions() throws {
        #expect(try BridgeRequest.parse(["action": "capabilities"]) == .capabilities)
        #expect(try BridgeRequest.parse(["action": "startDictation"]) == .startDictation)
        #expect(try BridgeRequest.parse(["action": "stopDictation"]) == .stopDictation)
    }

    @Test func parsesDraftInput() throws {
        let body: [String: Any] = [
            "action": "draftStory",
            "input": [
                "childName": "Ari",
                "mode": "series",
                "durationMinutes": 6,
                "customization": ["setting": "Moon bridge"],
                "storyBible": ["arcName": "Lantern Tides", "arcEpisodeNumber": 2],
            ],
        ]

        let request = try BridgeRequest.parse(body)

        guard case let .draftStory(input) = request else {
            Issue.record("Expected draftStory")
            return
        }
        #expect(input.childName == "Ari")
        #expect(input.durationMinutes == 6)
        #expect(input.customization?["setting"] == "Moon bridge")
        #expect(input.storyBible?.arcEpisodeNumber == 2)
    }

    @Test func rejectsMalformedMessages() {
        #expect(throws: BridgeRequestError.malformedMessage) { try BridgeRequest.parse("nope") }
        #expect(throws: BridgeRequestError.unknownAction("launch")) { try BridgeRequest.parse(["action": "launch"]) }
        #expect(throws: BridgeRequestError.missingInput) { try BridgeRequest.parse(["action": "draftStory"]) }
    }

    @Test func capabilitiesReplyMatchesWebContract() {
        let reply = NativeCapabilities(appleIntelligence: .modelNotReady, dictation: true).replyBody
        #expect(reply["appleIntelligence"] as? String == "modelNotReady")
        #expect(reply["dictation"] as? Bool == true)
    }

    @Test func dictationEventScriptEscapesTranscript() {
        let script = DictationEventScript.make(text: "Lu\"na'</script>", isFinal: false)
        #expect(script.hasPrefix("window.dispatchEvent(new CustomEvent('storytime:dictation'"))
        #expect(script.contains("\\\"na"))
        #expect(script.contains("\"isFinal\":false"))
    }
}

@Suite("StoryPromptBuilder")
struct StoryPromptBuilderTests {
    @Test(arguments: [(1, 3), (5, 3), (7, 4), (12, 6), (30, 8)])
    func sceneCountMatchesBackendBudget(duration: Int, expected: Int) {
        #expect(StoryPromptBuilder.sceneCount(forDurationMinutes: duration) == expected)
    }

    @Test func promptIncludesCustomizationAndContinuity() {
        let input = DraftStoryInput(
            childName: "  Ari ",
            mode: "series",
            durationMinutes: 6,
            customization: ["companionName": "Pip the fox", "mood": "   "],
            storyBible: StoryBibleInput(
                arcName: "Lantern Tides",
                arcEpisodeNumber: 2,
                arcObjective: nil,
                previousEpisodeSummary: "Ari found a glowing shell",
                recurringCharacter: nil,
                continuityFacts: nil
            )
        )

        let prompt = StoryPromptBuilder.prompt(for: input)

        #expect(prompt.contains("starring Ari in exactly 3 scenes"))
        #expect(prompt.contains("Companion: Pip the fox."))
        #expect(!prompt.contains("Mood:"))
        #expect(prompt.contains("episode 3 of the ongoing series \"Lantern Tides\""))
        #expect(prompt.contains("Previously: Ari found a glowing shell."))
    }

    @Test func promptCapsUserText() {
        let longName = String(repeating: "a", count: 500)
        let prompt = StoryPromptBuilder.prompt(for: DraftStoryInput(childName: longName, mode: "one-shot", durationMinutes: 5))
        #expect(!prompt.contains(String(repeating: "a", count: StoryPromptBuilder.maxFieldCharacters + 1)))
    }

    @Test func normalizedDropsBlankScenes() {
        #expect(StoryPromptBuilder.normalized(title: " Moon ", scenes: [" one ", " ", "two"])
            == StoryDraftReply(title: "Moon", scenes: ["one", "two"]))
        #expect(StoryPromptBuilder.normalized(title: "Empty", scenes: ["  "]) == nil)
    }
}
