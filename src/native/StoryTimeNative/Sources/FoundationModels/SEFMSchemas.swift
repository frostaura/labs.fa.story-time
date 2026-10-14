import Foundation
import FoundationModels

/// Guided-generation schemas. Fields are generated in declaration order, so every schema
/// opens with its planning field. Prose is NOT guided (schemas cost prompt tokens) — only
/// the outline, the scene digest and the poster choice are structured.
enum SEFMSchemas {
    /// Outline schema built at runtime so the scene array has EXACTLY the requested count
    /// (5, 8 or 12 depending on story length) — `@Guide(.count(n))` needs a literal.
    static func outline(sceneCount: Int) throws -> GenerationSchema {
        let n = min(max(sceneCount, 1), 16)
        let character = DynamicGenerationSchema(
            name: "Character",
            properties: [
                .init(name: "name", description: "A short, friendly first name", schema: .init(type: String.self)),
                .init(name: "kind", description: "What they are, in one or two words, like fox, owl or grandmother", schema: .init(type: String.self)),
                .init(name: "trait", description: "One gentle word, like curious or sleepy", schema: .init(type: String.self)),
            ])
        let scene = DynamicGenerationSchema(
            name: "Scene",
            properties: [
                .init(name: "summary", description: "One calm sentence: what happens in this scene", schema: .init(type: String.self)),
                .init(name: "beats", description: "Two or three short phrases, in order",
                      schema: .init(arrayOf: .init(type: String.self), minimumElements: 2, maximumElements: 3)),
            ])
        let root = DynamicGenerationSchema(
            name: "StoryOutline",
            properties: [
                .init(name: "plan", description: "One sentence planning the gentle arc from start to sleepy ending", schema: .init(type: String.self)),
                .init(name: "title", description: "The story title", schema: .init(type: String.self)),
                .init(name: "settings", description: "One to three places where the story happens",
                      schema: .init(arrayOf: .init(type: String.self), minimumElements: 1, maximumElements: 3)),
                .init(name: "characters", description: "One to four characters besides the child",
                      schema: .init(arrayOf: .init(referenceTo: "Character"), minimumElements: 1, maximumElements: 4)),
                .init(name: "scenes", description: "The scenes in order; the last one settles everyone safely for sleep",
                      schema: .init(arrayOf: .init(referenceTo: "Scene"), minimumElements: n, maximumElements: n)),
                .init(name: "theme", description: "The gentle lesson, in one short sentence", schema: .init(type: String.self)),
                .init(name: "bible", description: "Up to eight short facts to keep consistent: names, places, objects, colours",
                      schema: .init(arrayOf: .init(type: String.self), minimumElements: 0, maximumElements: 8)),
            ])
        return try GenerationSchema(root: root, dependencies: [character, scene])
    }
}

/// Running-memory digest of one scene: feeds the next scene's "story so far".
@Generable
struct SEFMSceneDigest {
    @Guide(description: "What happened in this scene, in at most two short sentences")
    var summary: String

    @Guide(description: "New facts to remember later, like a name, a place or an object; at most three", .maximumCount(3))
    var newFacts: [String]
}
