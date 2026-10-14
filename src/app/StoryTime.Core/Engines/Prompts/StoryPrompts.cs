using System.Text;
using StoryTime.Contracts;
using StoryTime.Poster.Spec;

namespace StoryTime.Core.Engines.Prompts;

/// <summary>
/// Engine-agnostic prompt text for any language-model story engine: age-banded system
/// prompts (the model-side layer of the kid-safety stack; StoryPipeline's SafetyFilter +
/// repair loop still checks every passage) and the per-stage user prompts (outline, scene, polish, repair, poster).
/// Plain strings with no chat-template markup — an engine wraps them in whatever session
/// shape its model expects (instructions + prompt for Apple Foundation Models).
/// </summary>
public static class StoryPrompts
{
    // ---------------- age-banded system prompts (write them well — they matter) ----------------

    private const string SharedRules =
        "Absolute rules, never broken for any reason: " +
        "no violence, no weapons, no fighting, no death or dying, no injury or blood, no illness, " +
        "no monsters or creatures presented as threats, no darkness presented as frightening, " +
        "no children lost or abandoned without an adult or friend gently nearby, no storms or disasters that hurt anyone, " +
        "no romance, no scary imagery of any kind, and nothing that could make a child anxious at bedtime. " +
        "Every character is kind or becomes kind; every problem is small, warm, and solved gently. " +
        "The story always winds down toward sleep: the final lines are calm, soft, and reassuring. " +
        "Never end on a cliffhanger. Never address the reader with questions. Write prose only — no headings, no lists, no notes.";

    /// <summary>Brave-ish theme widening (content filter level, additive 2026-07-30). This
    /// block ONLY widens the allowed-theme palette — SharedRules (the hard exclusions) are
    /// appended unchanged at every level, so no safety layer weakens with the level.</summary>
    private const string BraveishThemes =
        "Because this family chose the Brave-ish level, you may ALSO weave slightly more adventurous themes: " +
        "a small brave journey, a puzzle to solve, a storm watched safely from indoors, a lost thing found. " +
        "These are tiny thrills on gentle quests — every wobble is small, nobody is ever in danger, " +
        "and each thrill resolves warmly within a few sentences, well before the story winds down. ";

    public static string SystemPrompt(AgeBand band, ContentFilterLevel level = ContentFilterLevel.Gentle) => band switch
    {
        // Ages 3–4: the content filter level is deliberately ignored — Brave-ish tones down
        // automatically and this band's prompt is IDENTICAL to Gentle (kid-safety invariant).
        AgeBand.Age3To4 =>
            "You are StoryTime, a gentle bedtime storyteller for a very young child aged 3 to 4. " +
            "Use short sentences of five to ten words, and every few sentences let one stretch a little longer, " +
            "rocking gently like a lullaby, before returning to short and simple. Use only simple, everyday words a toddler knows: " +
            "words like warm, soft, little, sleepy, hug, home, moon, star. " +
            "Repeat comforting phrases and soft sounds the child can anticipate, like 'pit-pat', 'hush-hush', or 'one, two, three'. " +
            "Themes must stay inside this gentle list: bedtime routines, naptime, cuddly animals, family love, friendly helpers, " +
            "cozy homes, gentle weather, gardens, the moon and stars, small acts of kindness, favorite foods, lullabies. " +
            "There is no peril of any kind — not even mild suspense. Everything is safe the whole time. " + SharedRules,

        AgeBand.Age5To6 =>
            "You are StoryTime, a gentle bedtime storyteller for a child aged 5 to 6. " +
            "Use familiar words, with an occasional new word explained by the sentence around it. " +
            "Write long, flowing sentences: most sentences should be 20 to 30 words, wandering and curling with " +
            "'and' and 'while' and 'until', gathering soft little details as they go — and only occasionally land a short one for effect. " +
            "Never write three short sentences in a row, and never let short sentences dominate — flowing prose, not statements stitched together. " +
            "Themes must stay inside this gentle list: friendship, sharing, tiny adventures close to home, curious animals, " +
            "helping neighbors, seasons and nature, gentle magic like talking animals or glowing lanterns, " +
            "small mysteries with cozy answers, bedtime and dreams. " +
            (level == ContentFilterLevel.Braveish ? BraveishThemes : string.Empty) +
            "Curiosity is welcome, but nothing is ever dangerous: at most a moment of gentle wondering that resolves warmly within a few sentences. " + SharedRules,

        _ =>
            "You are StoryTime, a gentle bedtime storyteller for a child aged 7 to 8. " +
            "Use warm, vivid language with a slightly richer vocabulary, kept comfortable for reading aloud. " +
            "Write long, flowing sentences: most sentences should be 21 to 33 words, wandering and curling with " +
            "'and' and 'while' and 'until', gathering details as they go — and only occasionally land a short one for effect. " +
            "Never write three short sentences in a row, and never let short sentences dominate — the prose should flow, not read as statements stitched together. " +
            "Themes must stay inside this gentle list: friendship and teamwork, curiosity and discovery, quiet courage like trying something new, " +
            "imagination, nature and the night sky, kind inventions, gentle folklore, small journeys that end back home safe. " +
            (level == ContentFilterLevel.Braveish ? BraveishThemes : string.Empty) +
            "Gentle suspense is allowed — a wondering, a soft mystery — but it must always resolve kindly and soon, " +
            "and must never involve danger to anyone. " + SharedRules,
    };

    private static string Age(ChildProfile p) => p.Age.ToString();

    private static string Interests(ChildProfile p) =>
        p.Interests.Count > 0 ? string.Join(", ", p.Interests.Take(5)) : "cozy animals and the night sky";

    // ---------------- per-stage user prompts ----------------

    /// <summary>Title shape menu for the outline stage. A small on-device model anchors on
    /// literal example titles (it copies them verbatim — observed 2026-07-31: four different
    /// seeds all produced the example title), so the shapes are described abstractly and ONE
    /// is picked per weave by the engine (seeded), which is what actually rotates the shelf
    /// through different shapes night after night.</summary>
    public static readonly string[] TitleShapeHints =
    [
        "the companion character's name followed by the story's place",
        "the child's or companion's name, then 'and the', then a thing or creature from this story",
        "a gentle curious question about this story, ending with a question mark",
        "a title that begins with 'How'",
        "a doing-title that begins with an '-ing' word from this story's action",
        "one or two soft evocative words drawn from this story's imagery, nothing more",
    ];

    public static string OutlineUser(ChildProfile profile, StoryBible? bible, int sceneCount, string? titleShapeHint = null)
    {
        var sb = new StringBuilder();
        sb.Append($"Plan a bedtime story outline for {profile.Name}, age {Age(profile)}, who loves {Interests(profile)}. ");
        sb.Append($"The story has exactly {sceneCount} scenes. ");
        if (bible is not null && bible.EpisodeCount > 0)
        {
            sb.Append("This is the next episode of an ongoing gentle series. Keep these characters consistent: ");
            sb.Append(string.Join("; ", bible.Characters.Take(4).Select(c => $"{c.Name} the {c.Trait} {c.Kind}")));
            sb.Append(". Familiar places: ").Append(string.Join(", ", bible.Places.Take(4))).Append(". ");
            if (!string.IsNullOrWhiteSpace(bible.LastEpisodeRecap))
                sb.Append("Previously: ").Append(bible.LastEpisodeRecap).Append(' ');
        }
        sb.Append("Respond with compact JSON only, matching exactly this shape: ");
        sb.Append("{\"title\":...,\"moral\":...,\"settings\":[...],\"characters\":[{\"name\":...,\"kind\":...,\"trait\":...}],");
        sb.Append($"\"scenes\":[{{\"id\":1,\"summary\":...,\"beats\":[...]}}]}} with {sceneCount} scenes, ids 1..{sceneCount}. ");
        // Title craft (2026-07-31): a small model anchors hard on "The <Adjective> <Noun>"
        // unless steered, and it copies literal example titles verbatim — so the shape is a
        // per-weave seeded hint (TitleShapeHints, no examples) and the child's recent
        // titles are injected as negatives (threaded via ChildProfile.RecentTitles).
        sb.Append("The title is inviting and calm, and it is built only from this story's own characters, places, and things — never a generic phrase. ");
        sb.Append("Vary the title's shape. ");
        if (!string.IsNullOrEmpty(titleShapeHint))
            sb.Append("Tonight, shape the title as: ").Append(titleShapeHint).Append(". ");
        sb.Append("Never shape it as \"The <adjective> <noun>\". ");
        if (profile.RecentTitles.Count > 0)
        {
            sb.Append("This child's recent stories were titled: ");
            sb.Append(string.Join("; ", profile.RecentTitles.Take(8).Select(t => $"\"{t}\"")));
            sb.Append(". The new title must feel fresh beside those: do not reuse their distinctive words, and do not repeat their shapes. ");
        }
        sb.Append("The moral is one gentle sentence. Scene summaries are one sentence each; beats are 2-3 short phrases. ");
        sb.Append("The final scene always settles everyone down safely for sleep.");
        return sb.ToString();
    }

    public static string SceneUser(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail)
    {
        var sb = new StringBuilder();
        sb.Append($"Story: \"{outline.Title}\". Moral: {outline.Moral} ");
        sb.Append("Characters: ").Append(string.Join("; ", outline.Characters.Select(c => $"{c.Name} the {c.Trait} {c.Kind}"))).Append(". ");
        var isFinal = outline.Scenes.Count > 0 && scene.Id >= outline.Scenes[^1].Id;
        sb.Append($"Write scene {scene.Id} of {outline.Scenes.Count}: {scene.Summary} ");
        if (scene.Beats.Count > 0) sb.Append("Touch these beats in order: ").Append(string.Join("; ", scene.Beats)).Append(". ");
        if (!string.IsNullOrWhiteSpace(previousTail))
            sb.Append("The story so far ended with: \"...").Append(previousTail).Append("\" Continue smoothly from there without repeating it. ");
        sb.Append("Write 250 to 450 words of flowing prose. Let sentences run into one another gently rather than reading as ");
        sb.Append("short statements stitched together. Ground the scene in two or three concrete sensory details — a smell, ");
        sb.Append("a texture, a sound, a taste — and let one small, gentle surprise happen and resolve warmly. ");
        sb.Append("Do not include the title, scene numbers, or any headings. ");
        sb.Append(isFinal
            ? "This is the final scene: wind everything down gently and end with everyone cozy, safe, and drifting toward sleep."
            : "End the scene softly and contentedly, ready for the next scene to pick up — settled, never on a cliffhanger.");
        return sb.ToString();
    }

    public static string PolishUser(ChildProfile profile, string stitchedText) =>
        "Here is a full bedtime story. Polish it lightly: smooth any awkward transitions between scenes, " +
        "remove repetition, keep the wording age-appropriate, and keep the length about the same. " +
        // Flow channel (2026-07-31): the merge instruction is the polish pass's half of the
        // founder's "no stitched-together short sentences" fix — scenes ask for flow, polish
        // repairs whatever choppiness survived.
        "Wherever several short sentences sit in a row, merge them into one longer flowing sentence — " +
        "join them with 'and', 'while', 'until' or 'so that' — keeping the meaning and the calm exactly as they were. " +
        "Aim for an average sentence of at least twenty words: whenever two neighboring sentences are both short, merge them. " +
        // Pacing channel (2026-07-30): the narrator reads punctuation as pacing, so the
        // polish pass writes the pauses INTO the text — punctuation stays the single
        // source of truth between the LLM and the prosody director.
        "Shape the narration pacing with punctuation, used sparingly: let a dreamy moment trail off with an ellipsis…, " +
        "place an em-dash where the narrator should hold a tiny breath — like before a small gentle surprise, " +
        "keep exciting little moments in short sentences, vary sentence lengths so the reading breathes, " +
        "and keep paragraph breaks as calm resting points between scenes. " +
        "Make sure the closing paragraph is calm, gentle, and final — a soft goodnight with no loose ends. " +
        "Return only the polished story text, nothing else.\n\n" + stitchedText;

    /// <summary>Window-scoped polish (overlapped pipeline, 2026-07-31): same flow + pacing
    /// channels as PolishUser, but scoped to a ~2-scene passage. Only the FINAL window may
    /// close the story — middle windows are explicitly forbidden from ending it, so the
    /// per-window pass cannot scatter goodnights through the night's story.</summary>
    public static string PolishWindowUser(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow)
    {
        var sb = new StringBuilder();
        sb.Append(isFinalWindow
            ? "Here is the closing passage of a bedtime story. "
            : "Here is a passage from the middle of a bedtime story — the story continues after it. ");
        if (!string.IsNullOrWhiteSpace(previousTail))
            sb.Append("The story just before this passage ended with: \"...").Append(previousTail)
              .Append("\" Make the passage's opening flow smoothly from there without repeating it. ");
        sb.Append("Polish it lightly: smooth any awkward transitions, remove repetition, ");
        sb.Append("keep the wording age-appropriate, and keep the length about the same. ");
        sb.Append("Wherever several short sentences sit in a row, merge them into one longer flowing sentence — ");
        sb.Append("join them with 'and', 'while', 'until' or 'so that' — keeping the meaning and the calm exactly as they were. ");
        sb.Append("Aim for an average sentence of at least twenty words: whenever two neighboring sentences are both short, merge them. ");
        sb.Append("Shape the narration pacing with punctuation, used sparingly: let a dreamy moment trail off with an ellipsis…, ");
        sb.Append("place an em-dash where the narrator should hold a tiny breath — like before a small gentle surprise, ");
        sb.Append("vary sentence lengths so the reading breathes, and keep paragraph breaks as calm resting points between scenes. ");
        sb.Append(isFinalWindow
            ? "Make sure the closing paragraph is calm, gentle, and final — a soft goodnight with no loose ends. "
            : "Do NOT end the story here: no goodnight, no farewell, no closing moral — " +
              "the passage's last sentence stays settled and content, ready for the story to continue. ");
        sb.Append("Return only the polished passage text, nothing else.\n\n").Append(windowText);
        return sb.ToString();
    }

    public static string RepairUser(ChildProfile profile, string passage, string reason) =>
        $"This passage from a bedtime story was flagged: {reason}. " +
        "Rewrite it gently so the concern is fully removed while keeping the same characters and plot movement. " +
        "Keep roughly the same length. Return only the rewritten passage.\n\n" + passage;

    /// <summary>Poster spec contract v3 (docs/design/poster-engine.md): the model picks ONLY
    /// enum names — a scene, the light, a mood, a palette and up to three features from the
    /// story. No drawing, no positions: the procedural engine composes the picture. The
    /// "what fits where" list is generated from <see cref="PosterSpecRules.Compatibility"/>, so
    /// the prompt can never drift from the rules that repair the answer.</summary>
    public static string PosterUser(StoryOutline outline)
    {
        var sb = new StringBuilder();
        sb.Append($"Choose the poster picture for the bedtime story \"{outline.Title}\". ");
        if (outline.Settings.Count > 0) sb.Append("Where it happens: ").Append(string.Join(", ", outline.Settings.Take(3))).Append(". ");
        if (outline.Characters.Count > 0)
            sb.Append("Who is in it: ").Append(string.Join(", ", outline.Characters.Take(3).Select(c => c.Kind))).Append(". ");
        if (!string.IsNullOrWhiteSpace(outline.Moral)) sb.Append("What it is about: ").Append(outline.Moral.Trim()).Append(' ');

        sb.Append("\nScene, the place closest to the story: ");
        sb.Append("MoonlitSea open sea and shore; ForestClearing a clearing among pines; MountainLake a lake under mountains; ");
        sb.Append("FireflyMeadow soft rolling meadow; SnowyVillage snowy houses and pines; DesertOasis dunes and a palm pool; ");
        sb.Append("IslandCove a palm island beach; StarryHills quiet hills under stars; ReefAtDusk under the sea, coral and kelp; ");
        sb.Append("CloudKingdom up among the clouds; TownRooftops rooftops and chimneys; JungleCanopy big leaves and vines.");

        sb.Append("\nTimeOfDay: Dusk last warm glow; Twilight deep blue after sunset; Moonlit a bright moon; Starlit dark sky full of stars.");
        sb.Append("\nMood: Calm, Cozy, Dreamy, Magical, Sleepy or Curious, whichever the story feels like.");
        sb.Append("\nPalette: NightHarbor indigo harbour; MoonMeadow mossy moonlit green; OceanDeep deep teal sea; PlumDusk plum; ");
        sb.Append("PineForest dark evergreen; CloudDrift slate lavender; StarfieldViolet violet; LanternGlow warm lamplight brown; ");
        sb.Append("TidePool rock-pool green; SnowHush blue-grey winter; EmberSky warm ember dusk; MistValley cool grey mist.");

        sb.Append("\nFeatures: up to three things the story really has, most important first, at most one animal. ");
        sb.Append("Only things that belong in the chosen scene:");
        foreach (var scene in Enum.GetValues<PosterScene>())
        {
            var fits = PosterSpecRules.Compatibility
                .Where(kv => !SkyEverywhere.Contains(kv.Key) && kv.Value.Contains(scene))
                .Select(kv => kv.Key.ToString());
            sb.Append("\n").Append(scene).Append(": ").Append(string.Join(", ", fits)).Append('.');
        }
        sb.Append("\nBirds, Clouds, MilkyWay and ShootingStar fit any scene except ReefAtDusk; ShootingStar only when the story is about stars or wishes. ");
        sb.Append("Fewer is better: leave features out rather than add things the story does not have.");
        return sb.ToString();
    }

    private static readonly PosterFeature[] SkyEverywhere =
        [PosterFeature.Birds, PosterFeature.Clouds, PosterFeature.MilkyWay, PosterFeature.ShootingStar];
}
