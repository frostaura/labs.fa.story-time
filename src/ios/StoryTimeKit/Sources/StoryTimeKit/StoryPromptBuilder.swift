import Foundation

/// Pure prompt + budget logic for on-device drafting. Kept free of Apple-only
/// frameworks so it is unit-testable anywhere.
public enum StoryPromptBuilder {
    /// Mirrors backend `StoryTime:Generation` defaults (MinutesPerScene=2, Min=3, Max=8).
    /// The API re-applies its own cap, so this only keeps the on-device context small.
    public static let minutesPerScene = 2
    public static let minSceneCount = 3
    public static let maxSceneCount = 8
    public static let maxFieldCharacters = 120

    public static let instructions = """
    You are StoryTime, a gentle bedtime storyteller for young children.
    Write calm, kind, age-appropriate stories with no peril, violence, or scary imagery.
    Each scene is one short paragraph of two to four soothing sentences.
    The final scene always winds down toward sleep.
    """

    public static func sceneCount(forDurationMinutes durationMinutes: Int) -> Int {
        let bounded = max(durationMinutes, minutesPerScene)
        let estimate = Int((Double(bounded) / Double(minutesPerScene)).rounded(.up))
        return min(max(estimate, minSceneCount), maxSceneCount)
    }

    public static func prompt(for input: DraftStoryInput) -> String {
        let childName = sanitized(input.childName) ?? "the child"
        let sceneCount = sceneCount(forDurationMinutes: input.durationMinutes)
        var lines = [
            "Write a bedtime story starring \(childName) in exactly \(sceneCount) scenes.",
            "Give it a short, warm title.",
        ]

        if let customization = input.customization {
            let details: [(String, String)] = [
                ("Story arc", "arcName"),
                ("Companion", "companionName"),
                ("Setting", "setting"),
                ("Mood", "mood"),
                ("Narration style", "narrationStyle"),
            ]
            for (label, key) in details {
                if let value = sanitized(customization[key]) {
                    lines.append("\(label): \(value).")
                }
            }
        }

        if let bible = input.storyBible {
            if let arc = sanitized(bible.arcName) {
                let episode = (bible.arcEpisodeNumber ?? 0) + 1
                lines.append("This is episode \(episode) of the ongoing series \"\(arc)\".")
            }
            if let character = sanitized(bible.recurringCharacter) {
                lines.append("Recurring character: \(character).")
            }
            if let objective = sanitized(bible.arcObjective) {
                lines.append("Series goal: \(objective).")
            }
            if let previous = sanitized(bible.previousEpisodeSummary) {
                lines.append("Previously: \(previous).")
            }
        }

        return lines.joined(separator: "\n")
    }

    /// Trims, caps, and drops empty values so user text cannot balloon the context window.
    static func sanitized(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else {
            return nil
        }
        return trimmed.count <= maxFieldCharacters ? trimmed : String(trimmed.prefix(maxFieldCharacters))
    }

    public static func normalized(title: String, scenes: [String]) -> StoryDraftReply? {
        let cleanScenes = scenes
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        guard !cleanScenes.isEmpty else {
            return nil
        }
        return StoryDraftReply(title: title.trimmingCharacters(in: .whitespacesAndNewlines), scenes: cleanScenes)
    }
}
