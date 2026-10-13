#if os(iOS) && canImport(Speech)
import AVFoundation
import Foundation
import Speech

public enum OnDeviceDictationError: Error {
    case unsupportedLocale
    case microphonePermissionDenied
    case alreadyRunning
}

/// Native on-device dictation built on `SpeechAnalyzer` + `DictationTranscriber`.
/// Speech assets are system-managed via `AssetInventory`; the app ships and downloads no models.
@MainActor
public final class OnDeviceDictation {
    public typealias PartialHandler = @MainActor (_ text: String, _ isFinal: Bool) -> Void

    private let audioEngine = AVAudioEngine()
    private var inputBuilder: AsyncStream<AnalyzerInput>.Continuation?
    private var session: Task<String, Error>?

    public init() {}

    public static var isSupported: Bool {
        get async {
            await DictationTranscriber.supportedLocale(equivalentTo: Locale.current) != nil
        }
    }

    public var isRunning: Bool { session != nil }

    /// Starts listening and resolves with the final transcript once `stop()` is called.
    public func start(onPartial: @escaping PartialHandler) async throws -> String {
        guard session == nil else { throw OnDeviceDictationError.alreadyRunning }
        guard await AVAudioApplication.requestRecordPermission() else {
            throw OnDeviceDictationError.microphonePermissionDenied
        }
        guard let locale = await DictationTranscriber.supportedLocale(equivalentTo: Locale.current) else {
            throw OnDeviceDictationError.unsupportedLocale
        }

        let transcriber = DictationTranscriber(locale: locale, preset: .progressiveShortDictation)
        if let installation = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            try await installation.downloadAndInstall()
        }

        let analyzerFormat = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber])
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let (inputSequence, inputBuilder) = AsyncStream.makeStream(of: AnalyzerInput.self)
        self.inputBuilder = inputBuilder

        try startAudio(analyzerFormat: analyzerFormat, inputBuilder: inputBuilder)

        let task = Task<String, Error> { @MainActor in
            var finalized = ""
            var volatile = ""
            let results = Task { @MainActor in
                for try await result in transcriber.results {
                    let text = String(result.text.characters)
                    if result.isFinal {
                        finalized += text
                        volatile = ""
                    } else {
                        volatile = text
                    }
                    onPartial((finalized + volatile).trimmingCharacters(in: .whitespacesAndNewlines), result.isFinal)
                }
            }

            if let lastSampleTime = try await analyzer.analyzeSequence(inputSequence) {
                try await analyzer.finalizeAndFinish(through: lastSampleTime)
            } else {
                try analyzer.cancelAndFinishNow()
            }
            try await results.value
            return (finalized + volatile).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        session = task
        defer { session = nil }
        return try await task.value
    }

    public func stop() {
        audioEngine.inputNode.removeTap(onBus: 0)
        audioEngine.stop()
        inputBuilder?.finish()
        inputBuilder = nil
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func startAudio(
        analyzerFormat: AVAudioFormat?,
        inputBuilder: AsyncStream<AnalyzerInput>.Continuation
    ) throws {
        let audioSession = AVAudioSession.sharedInstance()
        try audioSession.setCategory(.record, mode: .measurement, options: .duckOthers)
        try audioSession.setActive(true, options: .notifyOthersOnDeactivation)

        let inputNode = audioEngine.inputNode
        let inputFormat = inputNode.outputFormat(forBus: 0)
        let converter = analyzerFormat.map { AnalyzerInputConverter(analyzerFormat: $0) }

        inputNode.installTap(onBus: 0, bufferSize: 4096, format: inputFormat) { buffer, _ in
            if let converter, let inputs = try? converter.convert(buffer, at: nil) {
                inputs.forEach { inputBuilder.yield($0) }
            } else {
                inputBuilder.yield(AnalyzerInput(buffer: buffer))
            }
        }

        audioEngine.prepare()
        try audioEngine.start()
    }
}
#endif
