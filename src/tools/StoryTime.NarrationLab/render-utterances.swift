// StoryTime voice lab — renders each planned utterance with macOS AVSpeechSynthesizer (the
// same engine iOS uses) to raw PCM, exactly the way the app's iOS renderer does:
//   * write(_:toBufferCallback:) per utterance, one at a time;
//   * zero-length buffers arrive at paragraph breaks, NOT only at the end — completion is
//     the delegate's didFinish, which fires in write mode;
//   * buffers are read by their real format (Float32 mono, or Int16), never assumed.
//
// usage:
//   swift render-utterances.swift plan  <plan.json> <voice-name> <out-dir>
//   swift render-utterances.swift baseline <story.txt> <voice-name> <out-dir>   (one utterance, rate 0.5)
//   swift render-utterances.swift voices
// Writes <out-dir>/NNNN.f32 (Float32 LE) + NNNN.json {"rate":…, "text":…, "voice":…}.

import AVFoundation
import Foundation

final class Writer: NSObject, AVSpeechSynthesizerDelegate {
    let synth = AVSpeechSynthesizer()
    var samples: [Float] = []
    var rate: Double = 0
    var finished = false

    override init() {
        super.init()
        synth.delegate = self
    }

    func speechSynthesizer(_ s: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) { finished = true }

    func render(_ utterance: AVSpeechUtterance) -> ([Float], Double) {
        samples = []
        rate = 0
        finished = false
        synth.write(utterance) { buffer in
            guard let pcm = buffer as? AVAudioPCMBuffer, pcm.frameLength > 0 else { return }  // zero-length: keep going
            let n = Int(pcm.frameLength)
            self.rate = pcm.format.sampleRate
            if let f = pcm.floatChannelData {
                self.samples.append(contentsOf: UnsafeBufferPointer(start: f[0], count: n))
            } else if let i = pcm.int16ChannelData {
                self.samples.append(contentsOf: UnsafeBufferPointer(start: i[0], count: n).map { Float($0) / 32768 })
            }
        }
        let deadline = Date().addingTimeInterval(120)
        while !finished && Date() < deadline {
            RunLoop.main.run(until: Date().addingTimeInterval(0.01))
        }
        // Let any last buffer callbacks drain.
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        return (samples, rate)
    }
}

func voice(named name: String) -> AVSpeechSynthesisVoice {
    let all = AVSpeechSynthesisVoice.speechVoices().filter { $0.language.hasPrefix("en") }
    let matches = all.filter { $0.name == name || $0.name.hasPrefix(name + " ") }
        .sorted { $0.quality.rawValue > $1.quality.rawValue }
    guard let v = matches.first else {
        FileHandle.standardError.write("no English voice named \(name)\n".data(using: .utf8)!)
        exit(3)
    }
    return v
}

func write(_ dir: URL, _ index: Int, _ samples: [Float], _ rate: Double, _ text: String, _ voice: AVSpeechSynthesisVoice) {
    let stem = dir.appendingPathComponent(String(format: "%04d", index))
    samples.withUnsafeBufferPointer { try! Data(buffer: $0).write(to: stem.appendingPathExtension("f32")) }
    let meta: [String: Any] = ["rate": Int(rate), "text": text, "voice": voice.identifier, "quality": voice.quality.rawValue]
    try! JSONSerialization.data(withJSONObject: meta).write(to: stem.appendingPathExtension("json"))
}

let args = CommandLine.arguments
guard args.count >= 2 else { print("usage: plan|baseline|voices …"); exit(2) }

if args[1] == "voices" {
    for v in AVSpeechSynthesisVoice.speechVoices() where v.language.hasPrefix("en") {
        print("\(v.identifier)\t\(v.name)\t\(v.language)\tq=\(v.quality.rawValue)\ttraits=\(v.voiceTraits.rawValue)")
    }
    exit(0)
}

guard args.count >= 5 else { print("usage: plan|baseline <in> <voice> <out-dir>"); exit(2) }
let out = URL(fileURLWithPath: args[4], isDirectory: true)
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let v = voice(named: args[3])
let writer = Writer()
FileHandle.standardError.write("voice: \(v.identifier) q=\(v.quality.rawValue)\n".data(using: .utf8)!)

if args[1] == "baseline" {
    let text = try! String(contentsOfFile: args[2], encoding: .utf8)
    let u = AVSpeechUtterance(string: text)
    u.voice = v
    u.rate = AVSpeechUtteranceDefaultSpeechRate
    let (s, r) = writer.render(u)
    write(out, 0, s, r, "baseline", v)
    print("baseline: \(s.count) samples @ \(r) Hz")
    exit(0)
}

struct Plan: Decodable {
    struct U: Decodable { let index: Int; let text: String; let rate: Float; let pitch: Float }
    let utterances: [U]
}
let plan = try! JSONDecoder().decode(Plan.self, from: Data(contentsOf: URL(fileURLWithPath: args[2])))
for u in plan.utterances {
    let utt = AVSpeechUtterance(string: u.text)
    utt.voice = v
    utt.rate = u.rate
    utt.pitchMultiplier = u.pitch
    utt.volume = 1
    utt.prefersAssistiveTechnologySettings = false
    let (s, r) = writer.render(utt)
    if s.isEmpty { FileHandle.standardError.write("utterance \(u.index) rendered nothing\n".data(using: .utf8)!) }
    write(out, u.index, s, r, u.text, v)
}
print("rendered \(plan.utterances.count) utterances with \(v.name)")
