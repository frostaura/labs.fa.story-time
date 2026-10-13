#if os(iOS)
import AVFoundation
import Foundation

public enum OnDeviceNarratorError: Error {
    case noVoiceAvailable
    case nothingToNarrate
    case renderFailed
}

public struct NarrationResult: Equatable, Sendable {
    public var teaserDurationSeconds: Double
    public var fullDurationSeconds: Double

    public var replyBody: [String: Any] {
        ["teaserDurationSeconds": teaserDurationSeconds, "fullDurationSeconds": fullDurationSeconds]
    }
}

/// Renders story voiceovers on-device with Apple speech synthesis (`AVSpeechSynthesizer.write`)
/// into compact AAC files cached in Application Support. Voices are system-managed; prefers
/// the highest-quality (premium/enhanced) voice installed for the user's language.
@MainActor
public final class OnDeviceNarrator {
    public static let retainedStoryCount = 40

    private let synthesizer = AVSpeechSynthesizer()
    private let directory: URL

    public init(directory: URL? = nil) {
        self.directory = directory ?? FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Narration", isDirectory: true)
    }

    public static var isSupported: Bool {
        preferredVoice() != nil
    }

    public static func preferredVoice(language: String = AVSpeechSynthesisVoice.currentLanguageCode()) -> AVSpeechSynthesisVoice? {
        let languagePrefix = String(language.prefix(2))
        let candidates = AVSpeechSynthesisVoice.speechVoices().filter { voice in
            voice.language.hasPrefix(languagePrefix) && !voice.voiceTraits.contains(.isNoveltyVoice)
        }
        let ranked = candidates.sorted { lhs, rhs in
            if lhs.quality != rhs.quality { return lhs.quality.rawValue > rhs.quality.rawValue }
            return lhs.language == language && rhs.language != language
        }
        return ranked.first ?? AVSpeechSynthesisVoice(language: language)
    }

    public func narrate(_ input: NarrateStoryInput) async throws -> NarrationResult {
        guard let voice = Self.preferredVoice() else { throw OnDeviceNarratorError.noVoiceAvailable }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        let style = NarrationVoiceStyle.from(narrationStyle: input.narrationStyle)
        var durations: [NarrationPart: Double] = [:]
        for part in NarrationPart.allCases {
            let segments = NarrationScript.segments(for: input, part: part)
            guard !segments.isEmpty else { throw OnDeviceNarratorError.nothingToNarrate }
            let url = fileURL(storyId: input.storyId, part: part)
            durations[part] = try await render(segments: segments, voice: voice, style: style, to: url)
        }
        pruneCache()
        return NarrationResult(
            teaserDurationSeconds: durations[.teaser] ?? 0,
            fullDurationSeconds: durations[.full] ?? 0
        )
    }

    /// Returns the cached narration as a data URI for the web player, or nil when missing.
    public func dataUri(storyId: String, part: NarrationPart) -> String? {
        guard NarrationScript.isValidStoryId(storyId),
              let data = try? Data(contentsOf: fileURL(storyId: storyId, part: part)) else {
            return nil
        }
        return "data:audio/mp4;base64,\(data.base64EncodedString())"
    }

    private func fileURL(storyId: String, part: NarrationPart) -> URL {
        directory.appendingPathComponent(NarrationScript.fileName(storyId: storyId, part: part))
    }

    private func render(
        segments: [String],
        voice: AVSpeechSynthesisVoice,
        style: NarrationVoiceStyle,
        to url: URL
    ) async throws -> Double {
        try? FileManager.default.removeItem(at: url)
        var file: AVAudioFile?
        var framesWritten: AVAudioFramePosition = 0
        var sampleRate = 22_050.0

        for (index, segment) in segments.enumerated() {
            let utterance = AVSpeechUtterance(string: segment)
            utterance.voice = voice
            utterance.rate = AVSpeechUtteranceDefaultSpeechRate * style.rateMultiplier
            utterance.pitchMultiplier = style.pitchMultiplier
            utterance.volume = style.volume

            let buffers = try await synthesize(utterance)
            for buffer in buffers {
                if file == nil {
                    sampleRate = buffer.format.sampleRate
                    file = try AVAudioFile(
                        forWriting: url,
                        settings: Self.aacSettings(sampleRate: sampleRate),
                        commonFormat: buffer.format.commonFormat,
                        interleaved: buffer.format.isInterleaved
                    )
                }
                try file?.write(from: buffer)
                framesWritten += AVAudioFramePosition(buffer.frameLength)
            }

            if index < segments.count - 1, let template = buffers.last,
               let silence = Self.silence(seconds: style.pauseSeconds, like: template.format) {
                try file?.write(from: silence)
                framesWritten += AVAudioFramePosition(silence.frameLength)
            }
        }

        guard file != nil, framesWritten > 0 else { throw OnDeviceNarratorError.renderFailed }
        file = nil // closes and finalizes the AAC container
        return Double(framesWritten) / sampleRate
    }

    private func synthesize(_ utterance: AVSpeechUtterance) async throws -> [AVAudioPCMBuffer] {
        try await withCheckedThrowingContinuation { continuation in
            var collected: [AVAudioPCMBuffer] = []
            var finished = false
            synthesizer.write(utterance) { buffer in
                guard !finished else { return }
                guard let pcm = buffer as? AVAudioPCMBuffer else { return }
                if pcm.frameLength == 0 {
                    finished = true
                    continuation.resume(returning: collected)
                    return
                }
                collected.append(Self.copy(pcm))
            }
        }
    }

    private func pruneCache() {
        let keys: Set<URLResourceKey> = [.contentModificationDateKey]
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: Array(keys)
        ) else { return }
        let sorted = files.sorted { lhs, rhs in
            let left = (try? lhs.resourceValues(forKeys: keys).contentModificationDate) ?? .distantPast
            let right = (try? rhs.resourceValues(forKeys: keys).contentModificationDate) ?? .distantPast
            return left > right
        }
        let retainedFiles = Self.retainedStoryCount * NarrationPart.allCases.count
        for stale in sorted.dropFirst(retainedFiles) {
            try? FileManager.default.removeItem(at: stale)
        }
    }

    private static func aacSettings(sampleRate: Double) -> [String: Any] {
        [
            AVFormatIDKey: kAudioFormatMPEG4AAC,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVEncoderBitRateKey: 32_000,
        ]
    }

    /// The synthesizer may reuse its buffers, so keep an owned copy.
    private static func copy(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer {
        guard let copy = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: buffer.frameLength) else {
            return buffer
        }
        copy.frameLength = buffer.frameLength
        let source = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: buffer.audioBufferList))
        let destination = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        for (from, to) in zip(source, destination) {
            if let fromData = from.mData, let toData = to.mData {
                memcpy(toData, fromData, Int(min(from.mDataByteSize, to.mDataByteSize)))
            }
        }
        return copy
    }

    private static func silence(seconds: Double, like format: AVAudioFormat) -> AVAudioPCMBuffer? {
        let frames = AVAudioFrameCount(max(0, seconds) * format.sampleRate)
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else {
            return nil
        }
        buffer.frameLength = frames
        for channel in UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList) {
            if let data = channel.mData {
                memset(data, 0, Int(channel.mDataByteSize))
            }
        }
        return buffer
    }
}
#endif
