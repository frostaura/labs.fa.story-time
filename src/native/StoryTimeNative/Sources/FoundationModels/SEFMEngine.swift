import Foundation
import FoundationModels

/// Objective-C facing shim over Apple's on-device Foundation Models, consumed by the .NET
/// binding (src/native/StoryTimeNative.Binding). Contract (docs/research/foundation-models.md):
///
/// - JSON in, JSON out. Requests carry `instructions`, `prompt`, `options` (and `sceneCount`
///   for the outline). Results are JSON strings; errors are an `SEFMErrorCode` plus a JSON
///   payload `{message, explanation?, resetDate?}`.
/// - Completion handlers, never `async`, and every handler is invoked on the MAIN queue.
/// - Every call returns a request id that `cancel(_:)` accepts. Cancellation is cooperative;
///   the completion still fires, with `.cancelled`.
/// - A FRESH `LanguageModelSession` per call: a session allows one request at a time and is
///   dead after a context overflow, so sessions are never reused.
/// - Stateless about stories: prompts, budgets and the failure ladder live in C#.
@objc(SEFMEngine)
public final class SEFMEngine: NSObject, @unchecked Sendable {
    @objc public static let shared = SEFMEngine()

    public typealias ResultHandler = @Sendable (String?, Int, String?) -> Void
    public typealias DeltaHandler = @Sendable (String) -> Void
    public typealias CountHandler = @Sendable (Int, Int) -> Void

    // Guarded by `lock`. Insertion happens while the lock is held across Task creation, so a
    // task that finishes instantly still finds (and removes) its own entry.
    private let lock = NSLock()
    private var tasks: [UUID: Task<Void, Never>] = [:]

    override private init() {
        super.init()
    }

    private var model: SystemLanguageModel { SystemLanguageModel.default }

    // MARK: - Availability

    /// `{"available": Bool, "reason": String, "contextSize": Int, "variant": String,
    /// "localeSupported": Bool}`. reason ∈ available | deviceNotEligible |
    /// appleIntelligenceNotEnabled | modelNotReady | unsupportedLocale | unknown.
    /// Stories are written in English, so locale support is checked for en_US.
    @objc public func availabilityJSON() -> String {
        let m = model
        let localeSupported = m.supportsLocale(Locale(identifier: "en_US"))
        var reason = "available"
        var available = false
        switch m.availability {
        case .available:
            available = localeSupported
            reason = localeSupported ? "available" : "unsupportedLocale"
        case .unavailable(let why):
            switch why {
            case .deviceNotEligible: reason = "deviceNotEligible"
            case .appleIntelligenceNotEnabled: reason = "appleIntelligenceNotEnabled"
            case .modelNotReady: reason = "modelNotReady"
            @unknown default: reason = "unknown"
            }
        }
        return SEFMJSON.object([
            "available": available,
            "reason": reason,
            "contextSize": m.contextSize,
            "variant": m.variant.displayName,
            "localeSupported": localeSupported,
        ])
    }

    /// Loads model assets ahead of the first request. `stage` is informational only (all
    /// stages share the one system model); the throwaway session is released afterwards.
    @objc(prewarmWithStage:)
    public func prewarm(stage: String) {
        guard case .available = model.availability else { return }
        LanguageModelSession(model: model).prewarm()
    }

    /// Exact token count via `tokenCount(for:)`. Completion `(count, code)`; `(-1, code)` on failure.
    @objc(countTokens:completion:)
    public func countTokens(_ text: String, completion: @escaping CountHandler) {
        let m = model
        Task {
            do {
                let n = try await m.tokenCount(for: text)
                DispatchQueue.main.async { completion(n, SEFMErrorCode.ok.rawValue) }
            } catch {
                let (code, _) = await SEFMErrorMapper.map(error)
                DispatchQueue.main.async { completion(-1, code.rawValue) }
            }
        }
    }

    // MARK: - Stages

    /// Guided outline (dynamic schema with exactly `sceneCount` scenes). Result: the
    /// generated content's JSON — {plan, title, settings, characters, scenes, theme, bible}.
    @objc(generateOutline:completion:)
    public func generateOutline(requestJSON: String, completion: @escaping ResultHandler) -> UUID {
        run(completion) { [model] in
            let req = try SEFMRequest.decode(requestJSON)
            let schema = try SEFMSchemas.outline(sceneCount: req.sceneCount ?? 5)
            let session = LanguageModelSession(model: model, instructions: req.instructions)
            let response = try await session.respond(to: req.prompt, schema: schema,
                                                     options: req.options?.generationOptions ?? GenerationOptions())
            return response.content.jsonString
        }
    }

    /// Streams one scene's prose (honours `guardrails`). `onDelta` receives appended text in
    /// order (main queue, coalesced);
    /// the completion carries `{"text": full}` — the authoritative text.
    @objc(streamScene:onDelta:completion:)
    public func streamScene(requestJSON: String, onDelta: @escaping DeltaHandler,
                            completion: @escaping ResultHandler) -> UUID {
        run(completion) {
            let req = try SEFMRequest.decode(requestJSON)
            let session = LanguageModelSession(model: req.model, instructions: req.instructions)
            let stream = session.streamResponse(to: req.prompt, options: req.options?.generationOptions ?? GenerationOptions())
            // Snapshots are cumulative and arrive every ~27 ms; deltas are coalesced and
            // delivered at most every 120 ms (plus a final flush) so callers are not flooded.
            var latest = ""
            var delivered = ""
            var lastFlush = ContinuousClock.now
            func flush() {
                guard latest.count > delivered.count, latest.hasPrefix(delivered) else {
                    delivered = latest
                    return
                }
                let delta = String(latest.dropFirst(delivered.count))
                delivered = latest
                DispatchQueue.main.async { onDelta(delta) }
            }
            for try await snapshot in stream {
                try Task.checkCancellation()
                latest = snapshot.content
                if ContinuousClock.now - lastFlush >= .milliseconds(120) {
                    flush()
                    lastFlush = ContinuousClock.now
                }
            }
            try Task.checkCancellation()
            flush()
            return SEFMJSON.encode(SEFMTextResult(text: latest))
        }
    }

    /// Guided scene digest. Result: {summary, newFacts}.
    @objc(digestScene:completion:)
    public func digestScene(requestJSON: String, completion: @escaping ResultHandler) -> UUID {
        run(completion) { [model] in
            let req = try SEFMRequest.decode(requestJSON)
            let session = LanguageModelSession(model: model, instructions: req.instructions)
            let response = try await session.respond(to: req.prompt, generating: SEFMSceneDigest.self,
                                                     options: req.options?.generationOptions ?? GenerationOptions())
            return response.rawContent.jsonString
        }
    }

    /// Plain-text single-turn rewrite: per-scene polish, and passage repair (honours
    /// `guardrails`). Result: {"text"}.
    /// (One generic text call serves both: the shim stays stateless and prompt-agnostic.)
    @objc(polishScene:completion:)
    public func polishScene(requestJSON: String, completion: @escaping ResultHandler) -> UUID {
        run(completion) {
            let req = try SEFMRequest.decode(requestJSON)
            let session = LanguageModelSession(model: req.model, instructions: req.instructions)
            let response = try await session.respond(to: req.prompt,
                                                     options: req.options?.generationOptions ?? GenerationOptions())
            return SEFMJSON.encode(SEFMTextResult(text: response.content))
        }
    }

    /// Guided poster choice. Result: {palette, skyLight, landscape, focal, accent, mood}.
    @objc(generatePoster:completion:)
    public func generatePoster(requestJSON: String, completion: @escaping ResultHandler) -> UUID {
        run(completion) { [model] in
            let req = try SEFMRequest.decode(requestJSON)
            let session = LanguageModelSession(model: model, instructions: req.instructions)
            let response = try await session.respond(to: req.prompt, generating: SEFMPosterChoice.self,
                                                     options: req.options?.generationOptions ?? GenerationOptions())
            return response.rawContent.jsonString
        }
    }

    /// Cancels an in-flight request; its completion fires with `.cancelled`. Unknown or
    /// already-finished ids are ignored.
    @objc(cancel:)
    public func cancel(_ requestId: UUID) {
        lock.lock()
        let task = tasks[requestId]
        lock.unlock()
        task?.cancel()
    }

    // MARK: - Plumbing

    private func run(_ completion: @escaping ResultHandler,
                     _ work: @escaping @Sendable () async throws -> String) -> UUID {
        let id = UUID()
        lock.lock()
        defer { lock.unlock() }
        tasks[id] = Task { [weak self] in
            do {
                let json = try await work()
                self?.finish(id)
                DispatchQueue.main.async { completion(json, SEFMErrorCode.ok.rawValue, nil) }
            } catch {
                let (code, payload) = await SEFMErrorMapper.map(Task.isCancelled ? CancellationError() : error)
                self?.finish(id)
                DispatchQueue.main.async { completion(nil, code.rawValue, payload) }
            }
        }
        return id
    }

    private func finish(_ id: UUID) {
        lock.lock()
        tasks[id] = nil
        lock.unlock()
    }
}
