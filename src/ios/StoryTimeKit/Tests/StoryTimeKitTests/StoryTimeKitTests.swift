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
        let reply = NativeCapabilities(appleIntelligence: .modelNotReady, dictation: true, narration: true).replyBody
        #expect(reply["appleIntelligence"] as? String == "modelNotReady")
        #expect(reply["dictation"] as? Bool == true)
        #expect(reply["narration"] as? Bool == true)
    }

    @Test func parsesNarrationRequests() throws {
        let narrate = try BridgeRequest.parse([
            "action": "narrateStory",
            "input": ["storyId": "abc123", "title": "Moon Boat", "scenes": ["One"], "narrationStyle": "whisper"],
        ])
        #expect(narrate == .narrateStory(NarrateStoryInput(storyId: "abc123", title: "Moon Boat", scenes: ["One"], narrationStyle: "whisper")))

        let load = try BridgeRequest.parse(["action": "loadNarration", "storyId": "abc123", "part": "full"])
        #expect(load == .loadNarration(storyId: "abc123", part: .full))
    }

    @Test func rejectsUnsafeNarrationStoryIds() {
        #expect(throws: BridgeRequestError.invalidStoryId) {
            try BridgeRequest.parse(["action": "loadNarration", "storyId": "../../secrets", "part": "full"])
        }
        #expect(throws: BridgeRequestError.invalidStoryId) {
            try BridgeRequest.parse([
                "action": "narrateStory",
                "input": ["storyId": "a/b", "title": "t", "scenes": ["s"]],
            ])
        }
        #expect(throws: BridgeRequestError.missingInput) {
            try BridgeRequest.parse(["action": "loadNarration", "storyId": "abc", "part": "bonus"])
        }
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

@Suite("NarrationScript")
struct NarrationScriptTests {
    private let input = NarrateStoryInput(
        storyId: "story-1",
        title: " Moon Boat ",
        recap: "Previously: the boat found a star.",
        scenes: ["First scene.", "  ", "Second scene."],
        narrationStyle: "calm"
    )

    @Test func fullNarrationReadsTitleRecapAndScenes() {
        #expect(NarrationScript.segments(for: input, part: .full)
            == ["Moon Boat", "Previously: the boat found a star.", "First scene.", "Second scene."])
    }

    @Test func teaserReadsTitleAndOpeningExcerpt() {
        #expect(NarrationScript.segments(for: input, part: .teaser) == ["Moon Boat", "First scene."])

        let long = String(repeating: "Soft waves hum. ", count: 40)
        let teaser = NarrationScript.segments(
            for: NarrateStoryInput(storyId: "s", title: "T", scenes: [long]),
            part: .teaser
        )
        #expect(teaser[1].count <= NarrationScript.teaserCharacterBudget)
        #expect(teaser[1].hasSuffix("."))
    }

    @Test func validatesStoryIds() {
        #expect(NarrationScript.isValidStoryId("0f3a9c_story-1"))
        #expect(!NarrationScript.isValidStoryId(""))
        #expect(!NarrationScript.isValidStoryId("../etc"))
        #expect(!NarrationScript.isValidStoryId("ünicode"))
        #expect(!NarrationScript.isValidStoryId(String(repeating: "a", count: 65)))
        #expect(NarrationScript.fileName(storyId: "abc", part: .teaser) == "abc-teaser.m4a")
    }

    @Test func voiceStyleFollowsNarrationStyle() {
        #expect(NarrationVoiceStyle.from(narrationStyle: nil) == .bedtime)
        #expect(NarrationVoiceStyle.from(narrationStyle: "Warm Whisper").rateMultiplier < NarrationVoiceStyle.bedtime.rateMultiplier)
        #expect(NarrationVoiceStyle.from(narrationStyle: "playful").pitchMultiplier > 1)
    }
}
