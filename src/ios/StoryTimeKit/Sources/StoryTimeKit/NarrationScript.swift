import Foundation

/// Voice delivery derived from the story's narration style (pure, platform-neutral).
public struct NarrationVoiceStyle: Equatable, Sendable {
    /// Multiplier applied to the system default speech rate.
    public var rateMultiplier: Float
    public var pitchMultiplier: Float
    public var volume: Float
    /// Silence inserted between segments, in seconds.
    public var pauseSeconds: Double

    public static let bedtime = NarrationVoiceStyle(rateMultiplier: 0.85, pitchMultiplier: 1.0, volume: 0.9, pauseSeconds: 0.8)

    public static func from(narrationStyle: String?) -> NarrationVoiceStyle {
        let style = narrationStyle?.lowercased() ?? ""
        var voice = NarrationVoiceStyle.bedtime
        if ["whisper", "soft", "gentle", "sleepy", "hush"].contains(where: style.contains) {
            voice.rateMultiplier = 0.78
            voice.pitchMultiplier = 0.95
            voice.volume = 0.8
            voice.pauseSeconds = 1.0
        } else if ["playful", "cheer", "lively", "bright", "excited"].contains(where: style.contains) {
            voice.rateMultiplier = 0.95
            voice.pitchMultiplier = 1.08
            voice.pauseSeconds = 0.6
        }
        return voice
    }
}

/// Builds what gets spoken for each narration part.
public enum NarrationScript {
    /// Teaser previews the title plus the opening of the first scene.
    public static let teaserCharacterBudget = 280

    public static func segments(for input: NarrateStoryInput, part: NarrationPart) -> [String] {
        let title = clean(input.title)
        let scenes = input.scenes.compactMap(clean)
        switch part {
        case .teaser:
            guard let first = scenes.first else {
                return title.map { [$0] } ?? []
            }
            return [title, teaserExcerpt(first)].compactMap { $0 }
        case .full:
            return [title, clean(input.recap)].compactMap { $0 } + scenes
        }
    }

    /// Story ids are server-issued hex/slug identifiers; anything else is refused before touching the file system.
    public static func isValidStoryId(_ storyId: String) -> Bool {
        guard (1...64).contains(storyId.count) else { return false }
        return storyId.unicodeScalars.allSatisfy { scalar in
            CharacterSet.alphanumerics.contains(scalar) && scalar.isASCII || scalar == "-" || scalar == "_"
        }
    }

    public static func fileName(storyId: String, part: NarrationPart) -> String {
        "\(storyId)-\(part.rawValue).m4a"
    }

    static func teaserExcerpt(_ scene: String) -> String {
        guard scene.count > teaserCharacterBudget else { return scene }
        let prefix = String(scene.prefix(teaserCharacterBudget))
        if let sentenceEnd = prefix.lastIndex(where: { ".!?".contains($0) }) {
            return String(prefix[...sentenceEnd])
        }
        return prefix.trimmingCharacters(in: .whitespaces) + "…"
    }

    private static func clean(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else {
            return nil
        }
        return trimmed
    }
}
