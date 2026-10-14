// macOS lab host for the StoryTimeNative Foundation Models shim. Compiled together with
// ../../StoryTimeNative/Sources/FoundationModels/*.swift (see ../run-fm-lab.sh), so the Mac
// runs the exact shim code the iOS framework ships.
//
// Protocol: JSON lines. stdin  {"id": Int, "op": String, "json": String?}
//   op ∈ availability | prewarm | count | outline | scene | digest | polish | poster | cancel
// stdout {"id": Int, "kind": "delta", "text": String}
//        {"id": Int, "kind": "done", "code": Int, "result": String?, "error": String?}
import Foundation

let engine = SEFMEngine.shared
let outputLock = NSLock()
let requestsLock = NSLock()
nonisolated(unsafe) var requests: [Int: UUID] = [:]

func emit(_ dict: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: dict), let line = String(data: data, encoding: .utf8) else { return }
    outputLock.lock()
    FileHandle.standardOutput.write(Data((line + "\n").utf8))
    outputLock.unlock()
}

func done(_ id: Int) -> @Sendable (String?, Int, String?) -> Void {
    { result, code, error in
        requestsLock.lock(); requests[id] = nil; requestsLock.unlock()
        var d: [String: Any] = ["id": id, "kind": "done", "code": code]
        if let result { d["result"] = result }
        if let error { d["error"] = error }
        emit(d)
    }
}

func track(_ id: Int, _ uuid: UUID) {
    requestsLock.lock(); requests[id] = uuid; requestsLock.unlock()
}

func handle(_ line: String) {
    guard let obj = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
          let id = obj["id"] as? Int, let op = obj["op"] as? String else { return }
    let json = obj["json"] as? String ?? "{}"
    switch op {
    case "availability":
        emit(["id": id, "kind": "done", "code": 0, "result": engine.availabilityJSON()])
    case "prewarm":
        engine.prewarm(stage: json)
        emit(["id": id, "kind": "done", "code": 0, "result": "{}"])
    case "count":
        engine.countTokens(json) { n, code in emit(["id": id, "kind": "done", "code": code, "result": "\(n)"]) }
    case "outline":
        track(id, engine.generateOutline(requestJSON: json, completion: done(id)))
    case "scene":
        track(id, engine.streamScene(requestJSON: json, onDelta: { emit(["id": id, "kind": "delta", "text": $0]) }, completion: done(id)))
    case "digest":
        track(id, engine.digestScene(requestJSON: json, completion: done(id)))
    case "polish":
        track(id, engine.polishScene(requestJSON: json, completion: done(id)))
    case "poster":
        track(id, engine.generatePoster(requestJSON: json, completion: done(id)))
    case "cancel":
        requestsLock.lock(); let uuid = requests[Int(json) ?? -1]; requestsLock.unlock()
        if let uuid { engine.cancel(uuid) }
        emit(["id": id, "kind": "done", "code": 0, "result": "{}"])
    default:
        emit(["id": id, "kind": "done", "code": 99, "error": "{\"message\":\"unknown op\"}"])
    }
}

// On stdin EOF, exit once every in-flight request has completed (all on the main queue).
func exitWhenIdle() {
    requestsLock.lock(); let busy = !requests.isEmpty; requestsLock.unlock()
    if busy { DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { exitWhenIdle() } } else { exit(0) }
}

Thread.detachNewThread {
    while let line = readLine() {
        if !line.isEmpty { DispatchQueue.main.async { handle(line) } }
    }
    DispatchQueue.main.async { exitWhenIdle() }
}
dispatchMain()
