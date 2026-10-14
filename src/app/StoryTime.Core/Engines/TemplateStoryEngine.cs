using System.Text;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Engines;

/// <summary>Deterministic, seedable, fully-offline story generator — the permanent
/// demo/degraded path (never deleted; see program invariants). Curated outline skeletons are
/// parameterized by profile interests, prose is slot-filled from age-banded sentence
/// templates, and every story ends with a calm sleepy paragraph. Same seed → same story.</summary>
public sealed class TemplateStoryEngine : IStoryEngine
{
    private readonly Func<int>? _seedProvider;

    /// <param name="seedProvider">Supplies the per-story seed at outline time. Null → a
    /// deterministic seed derived from the profile and episode count (used for the fixed
    /// welcome story and in tests).</param>
    public TemplateStoryEngine(Func<int>? seedProvider = null) => _seedProvider = seedProvider;

    public string Name => "Template";

    // ---------- Word budgets (per design: 5/10/15 min → ~700/1400/2000 words) ----------

    public static int TargetWords(StoryLength length) => length switch
    {
        StoryLength.Five => 700,
        StoryLength.Ten => 1400,
        _ => 2000,
    };

    public static int SceneCount(StoryLength length) => length switch
    {
        StoryLength.Five => 5,
        StoryLength.Ten => 8,
        _ => 12,
    };

    // ---------- Curated content ----------

    private static readonly string[] FriendNames =
        ["Pip", "Luna", "Milo", "Hazel", "Wren", "Sora", "Nia", "Fen", "Tilly", "Bo"];

    private sealed record Theme(string Place, string PlaceB, string Creature, string Thing, string Sound, string Adj);

    private static readonly Dictionary<string, Theme> Themes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Foxes"] = new("the fern hollow", "the mossy den", "fox", "copper lantern", "soft rustle", "russet"),
        ["Ocean"] = new("the silver shore", "the tide pools", "otter", "spiral shell", "gentle wash of waves", "sea-glass"),
        ["Magic"] = new("the wishing meadow", "the old willow", "star sprite", "glimmer jar", "tiny chime", "shimmering"),
        ["Space"] = new("the quiet edge of the sky", "the moon's front porch", "moon moth", "comet ribbon", "hum of starlight", "silvery"),
        ["Forest"] = new("the pine glade", "the cedar bridge", "hedgehog", "acorn cup", "sigh of tall trees", "evergreen"),
        ["Clouds"] = new("the cloud meadow", "the drowsy sky harbor", "cloud lamb", "feather pillow", "hush of high wind", "wool-white"),
        ["Stars"] = new("the starlit hill", "the constellation garden", "firefly", "night-light bead", "twinkle-soft chime", "glowing"),
        ["Music"] = new("the humming valley", "the lullaby workshop", "songbird", "wooden music box", "slow sweet melody", "mellow"),
        ["Trains"] = new("the sleepy junction", "the lantern platform", "station cat", "brass whistle", "far-away chuff", "cozy"),
        ["Gardens"] = new("the walled garden", "the greenhouse of moons", "garden mouse", "seed of moonflowers", "drip of a watering can", "dewy"),
    };

    private static readonly Theme DefaultTheme =
        new("the meadow by the window", "the old oak", "owl", "woollen blanket", "evening breeze", "quiet");

    private sealed record Skeleton(string TitleTemplate, string Moral, string[] Beats);

    // ~10 outline skeletons. {name}=child, {friend}=companion, {creature}, {place}, {thing}.
    private static readonly Skeleton[] Skeletons =
    [
        new("The Lantern {Creature}", "even small lights make the dark feel friendly",
        [
            "{name} finds a tiny lantern glowing near {place}",
            "a {adj} {creature} named {friend} asks for help carrying the light home",
            "they follow the {sound} through {placeB}",
            "the lantern flickers low and they cup it together to keep it warm",
            "little lights answer back from every window and burrow",
            "the {creature} family welcomes them with warm honey tea",
            "{friend} tucks the lantern into its nook where it glows just right",
            "everyone says a slow, sleepy thank-you under the stars",
        ]),
        new("A Gift for {friend}", "the best gifts are made with time and care",
        [
            "{name} wants to make something special for {friend}",
            "they search {place} for just-right pieces",
            "the {creature} of {placeB} offers a {thing} to borrow",
            "the gift will not stick together and {name} takes a slow breath",
            "a {sound} gives {name} a gentle idea",
            "piece by piece the gift becomes lovely and a little wobbly",
            "{friend} loves it exactly because it is wobbly",
            "they place it where the moon can admire it too",
        ]),
        new("The Softest Sound", "quiet listening finds the sweetest things",
        [
            "{name} and {friend} wonder what the softest sound in the world is",
            "they listen at {place} and hear the {sound}",
            "the {creature} suggests trying {placeB} at dusk",
            "each sound is lovely but not quite the softest",
            "they sit very still with cocoa and listen to everything at once",
            "the softest sound turns out to be a sleepy heart beating slow",
            "they hum along with it all the way home",
            "the whole valley hums back, low and kind",
        ]),
        new("The Sleepy Festival of {place}", "slowing down together is its own celebration",
        [
            "an invitation drifts to {name}'s window from {place}",
            "{friend} the {creature} arrives to walk together",
            "paper lanterns bob along the path through {placeB}",
            "everyone moves slower and slower as the festival begins",
            "there is warm bread, soft songs, and a {thing} for every guest",
            "{name} helps the littlest {creature} find a cushion",
            "the festival ends with one long communal yawn",
            "stars come out to watch everyone amble home",
        ]),
        new("Helping {friend} Home", "kindness makes every path shorter",
        [
            "a small {creature} named {friend} has wandered far from {placeB}",
            "{name} offers a hand and a bite of biscuit",
            "they read the {sound} like a map",
            "a stream must be crossed on smooth stepping stones, one careful hop at a time",
            "{friend} gets tired so {name} carries the {thing} for a while",
            "familiar smells of home drift closer",
            "the {creature} family waves from the doorway of {placeB}",
            "{name} walks home under a sky full of slow, friendly stars",
        ]),
        new("The Night Garden of {placeB}", "wonderful things grow while we rest",
        [
            "{name} hears that a garden flowers only at bedtime",
            "{friend} the {creature} knows the mossy way in",
            "moonflowers open one by one as they tiptoe past",
            "a {thing} waits in the middle of the garden like a promise",
            "they water the sleepiest flowers with a tiny silver can",
            "the garden hums the {sound} as a thank-you",
            "each flower folds up, tucked in by the breeze",
            "{name} promises to visit again in a dream",
        ]),
        new("The Coziest Nest", "a home is built out of many small soft things",
        [
            "{friend} the {creature} needs a warmer place to sleep",
            "{name} and {friend} gather soft things around {place}",
            "the {sound} points them to thistledown near {placeB}",
            "they fold and tuck and pat everything into place",
            "the nest is too small until they widen it laughing softly",
            "neighbors bring one feather each to say welcome",
            "{friend} tries the nest and sighs the happiest sigh",
            "everyone whispers goodnight from their own snug corners",
        ]),
        new("The Moonlit Picnic", "everything tastes better shared under the moon",
        [
            "{name} packs a basket as the sun gets sleepy",
            "{friend} the {creature} brings a {thing} to share",
            "they spread a blanket at {place}",
            "the moon rises like a warm biscuit over {placeB}",
            "they trade tiny bites and even tinier jokes",
            "a family of {creature}s joins with berry jam",
            "crumbs are brushed away and the blanket becomes a cape",
            "they watch the moon until their blinks grow long",
        ]),
        new("The Quiet Wish", "wishes grow best when they are patient",
        [
            "{name} finds a wishing {thing} by {place}",
            "{friend} the {creature} explains how quiet wishes work",
            "they carry the wish gently through {placeB}",
            "the wish almost slips away on the breeze and they steady it together",
            "an old {creature} shares a story about waiting kindly",
            "the wish settles like a seed into soft ground",
            "a first tiny glimmer of the wish peeks out",
            "{name} pats the ground goodnight and promises to check tomorrow",
        ]),
        new("The Slow River Boat", "drifting slowly lets you see everything kind",
        [
            "a little boat waits for {name} and {friend} at {place}",
            "the river moves like it has nowhere to hurry to",
            "lantern buoys blink hello along {placeB}",
            "a {creature} on the bank waves with a {thing}",
            "they drift under bridges that hum the {sound}",
            "fireflies stitch tiny lights over the water",
            "the boat noses into a bed of reeds like a pillow",
            "the river rocks everyone gently toward sleep",
        ]),
    ];

    /// <summary>Gently-adventurous skeletons, offered ONLY at the Brave-ish content filter
    /// level (and never for ages 3–4 — EffectiveContentFilter tones that band down to
    /// Gentle). Tiny thrills on gentle quests, every wobble resolved within a beat or two,
    /// same calm sleepy ending as every other skeleton. The safety filter and repair loop
    /// are untouched by the level. Additive, 2026-07-30.</summary>
    private static readonly Skeleton[] BraveishSkeletons =
    [
        new("The Small Brave Journey", "a little courage and a good friend can find anything",
        [
            "{friend} the {creature} has misplaced a treasured {thing} somewhere past {place}",
            "{name} takes a deep breath and offers to help look, even though the path is new",
            "they follow small clues — a thread of {sound}, a nibbled biscuit, a friendly paw print",
            "a wobbly log bridge must be crossed, one careful step at a time, holding hands",
            "on the far side they laugh with relief and share the last biscuit",
            "the {thing} is found safe in a mossy nook, exactly where the clues pointed",
            "the walk home feels easy now, and {placeB} glows warm to welcome them",
            "everyone tucks the {thing} in its place and yawns a proud, sleepy yawn",
        ]),
        new("The Night the Sky Grumbled", "a storm outside makes the inside twice as cozy",
        [
            "far-off clouds grumble softly while {name} and {friend} play near {place}",
            "they feel the first fat raindrops and hurry inside {placeB}, giggling",
            "from the window seat the storm is only a show — flickers far away and rain going pit-pat",
            "they count the slow rumbles together and each one is further away than the last",
            "warm cocoa is poured and the {creature} tells a story about rain being the sky's lullaby",
            "the grumbles fade into gentle rain, then into drips, then into quiet",
            "the clouds part to show one bright washed star",
            "snug in blankets by the window, everyone drifts off to the last soft drips",
        ]),
        new("The Puzzle of {placeB}", "a puzzle shared is already half solved",
        [
            "{name} and {friend} find a curious riddle carved on an old signpost near {place}",
            "the riddle asks for three things: something soft, something singing, something that glows",
            "they hunt for the soft thing and find thistledown by the path",
            "the singing thing turns out to be the {sound} — it was with them all along",
            "for the glowing thing, the {creature} of {placeB} kindly lends a {thing}",
            "piece by piece the answer clicks together and the old signpost seems to smile",
            "the riddle's reward is the coziest view in {placeB}, just right for stargazing",
            "they solve one last riddle — who is sleepiest? — and everyone wins at once",
        ]),
    ];

    // Age-banded sentence templates. {S} slot = beat clause. 3–4 gets short simple sentences.
    private static readonly string[] FillersYoung =
    [
        "{name} smiled.",
        "The {creature} blinked slowly.",
        "It was very cozy.",
        "{friend} held {name}'s hand.",
        "The night was soft…",
        "They walked on quiet toes.",
        "A {adj} light glowed nearby.",
        "{name} took a big slow breath.",
        "The {sound} was kind.",
        "Everything felt safe and warm.",
        "{friend} gave a little nod.",
        "The moon watched over them.",
        "One star winked hello.",
        "{name} felt brave and calm.",
        "The {thing} felt smooth and warm.",
        "They shared a tiny giggle.",
        "The moon smiled down at them both.",
        "They hummed a tiny sleepy song together.",
    ];

    private static readonly string[] FillersMiddle =
    [
        "{name} listened carefully to the {sound} drifting by.",
        "The {creature} padded along beside them, whiskers twitching happily.",
        "Everything in {place} seemed to glow a gentle {adj} color.",
        "{friend} pointed at the sky, where the first stars were stretching awake.",
        "A warm breeze folded around them — soft as a favorite blanket.",
        "They stepped softly, so the sleeping flowers would not stir.",
        "The {thing} felt just right in {name}'s hands.",
        "Somewhere far away, the {sound} answered like an echo of a lullaby.",
        "{name} whispered a small thank-you to the evening.",
        "The path ahead curled like a ribbon of moonlight.",
        "{friend} hummed a tune that matched their footsteps.",
        "Little lights blinked on, one by one, all across {placeB}…",
        "The air smelled of warm bread and rain-washed leaves.",
        "{name} felt the calm settle over them like snow that is not cold.",
        "They paused to watch a leaf drift down, in no hurry at all.",
        "The {creature} curled its tail into a neat, contented circle.",
        // Flowing long variants (2026-07-31): sentences that wander and curl with 'and',
        // 'while' and 'until', so template prose reads like a storyteller, not stitching.
        "They followed the winding path while the {sound} drifted along beside them, and every soft step felt a little slower and a little cozier than the one before.",
        "{friend} told a small story about {placeB} while they walked, and {name} listened until the words and the footsteps folded into one gentle rhythm.",
        "The evening settled around them like a blanket being smoothed, and the {adj} light lingered on every leaf until the whole world seemed to hum with quiet.",
        "{name} trailed a hand through the cool soft air while the {creature} padded ahead, and together they let the path decide how slowly they would go.",
        "Somewhere above, the moon drifted out from behind a cloud and poured its {adj} light over {place}, until even the pebbles underfoot looked tucked in for the night.",
        // Short landing sentences — the 'then land a short one' half of the rhythm.
        "The night hummed softly on.",
        "They smiled at that.",
    ];

    private static readonly string[] FillersOlder =
    [
        "{name} noticed how the {sound} seemed to slow the whole evening down, as if the world were breathing out.",
        "The {creature} tilted its head thoughtfully, then trotted ahead to show them the gentlest way through {placeB}.",
        "Above them, the sky turned the deep {adj} color that only appears when day is tucking itself in.",
        "{friend} said that {place} always kept a little kindness ready for anyone who visited politely.",
        "They walked without hurrying, because some paths are nicer when you let them take their time.",
        "The {thing} caught the last of the light and held it, glowing softly like a memory of afternoon.",
        "{name} thought about how big the night was, and how friendly it felt anyway.",
        "A ribbon of mist curled along the ground, smoothing every stone into a soft gray pillow.",
        "The {creature} explained, in its unhurried way, that everything important can wait until morning.",
        "{friend} and {name} matched their breathing to the {sound}, slow and even and easy.",
        "Lanterns swayed gently overhead, stitching warm circles of light along the path.",
        "{name} felt the pleasant heaviness that comes at the end of a good and gentle day.",
        "The stars arranged themselves patiently, as if settling into their favorite chairs…",
        "Even the shadows here were soft — more like folded blankets than anything else.",
        // Flowing long variants (2026-07-31) — wander, curl, gather detail.
        "{friend} pointed out how the lanterns and the fireflies seemed to share the work of lighting the path, and {name} decided that was exactly how kindness worked too — quietly, and everywhere at once.",
        "They wandered on without any hurry while the {sound} wove itself between the branches, past sleepy flowers and smooth warm stones, until {placeB} opened before them like a page in a well-loved book.",
        "{name} breathed in the smell of moss and rain and something faintly sweet, and held it the way you hold a good thought, letting the evening grow rounder and softer all around them.",
        // Short landing sentences for rhythm.
        "The night agreed.",
        "So they stayed a while.",
    ];

    private static readonly string[] SleepyClosings =
    [
        "At last, {name} snuggled down, warm and heavy-eyed. The {sound} hushed into a lullaby, " +
        "and {friend} whispered, \"Goodnight, {name}.\" The stars dimmed their lights, one by one, " +
        "the whole world tucked itself in, and {name} drifted into the softest, safest sleep… Goodnight.",
        "And so the day folded itself up like a favorite blanket. {name} yawned a long, happy yawn, " +
        "and {friend} yawned back. \"Sleep well,\" the {creature} murmured. The moon pulled the covers " +
        "of night up over {place}, and {name} closed both eyes and slept, calm and cozy and loved. Goodnight.",
        "The evening grew quieter and quieter until it was just breath and starlight. {name} curled up, " +
        "{friend} settled close, and the {sound} rocked them both gently. \"Goodnight,\" whispered the night, " +
        "and {name} smiled a sleepy smile and sailed softly into dreams. Goodnight.",
    ];

    // ---------- IStoryEngine ----------

    public Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var seed = _seedProvider?.Invoke() ?? DeriveSeed(profile, bible?.EpisodeCount ?? 0);
        var rng = new DeterministicRandom(seed);

        var theme = PickTheme(profile, rng);
        // Brave-ish widens the skeleton pool (appended AFTER the gentle set so Gentle
        // draws stay seed-identical to before the level existed). Ages 3–4 never widen.
        var pool = profile.EffectiveContentFilter == ContentFilterLevel.Braveish
            ? Skeletons.Concat(BraveishSkeletons).ToArray()
            : Skeletons;
        var skeleton = pool[rng.Next(pool.Length)];

        // Companion: reuse the bible's lead recurring character in series mode, else curated pool.
        string friendName;
        string creatureKind = theme.Creature;
        if (bible is { Characters.Count: > 0 })
        {
            var recurring = bible.Characters[0];
            friendName = recurring.Name;
            if (!string.IsNullOrEmpty(recurring.Kind)) creatureKind = recurring.Kind;
        }
        else
        {
            friendName = FriendNames[rng.Next(FriendNames.Length)];
        }

        var slots = BuildSlots(profile.Name, friendName, theme with { Creature = creatureKind });

        var outline = new StoryOutline
        {
            // Title comes from the shape-diverse composer (2026-07-31), on a FORKED rng so
            // every other draw in this method stays seed-identical to before the change.
            Title = ComposeTitle(new DeterministicRandom(HashCombine(seed, 9973)), skeleton, slots, profile.RecentTitles),
            Moral = skeleton.Moral,
            Seed = seed,
            Settings = [theme.Place, theme.PlaceB],
            Characters =
            [
                new CharacterCard { Name = profile.Name, Kind = "child", Trait = "curious" },
                new CharacterCard { Name = friendName, Kind = creatureKind, Trait = "gentle" },
            ],
        };

        for (var i = 0; i < sceneCount; i++)
        {
            var beat = skeleton.Beats[i % skeleton.Beats.Length];
            outline.Scenes.Add(new SceneOutline
            {
                Id = i,
                Summary = Fill(beat, slots),
                Beats = [Fill(beat, slots)],
            });
        }
        return Task.FromResult(outline);
    }

    public Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var rng = new DeterministicRandom(HashCombine(outline.Seed, scene.Id + 1));
        var theme = PickTheme(profile, new DeterministicRandom(outline.Seed));
        var friend = outline.Characters.Count > 1 ? outline.Characters[1] : new CharacterCard { Name = "Pip", Kind = theme.Creature };
        var slots = BuildSlots(profile.Name, friend.Name, theme with { Creature = friend.Kind is { Length: > 0 } k ? k : theme.Creature });

        var totalScenes = Math.Max(1, outline.Scenes.Count);
        var isFinal = scene.Id == totalScenes - 1;
        var target = TargetWords(profile.Length) / totalScenes;

        var fillers = profile.Band switch
        {
            AgeBand.Age3To4 => FillersYoung,
            AgeBand.Age5To6 => FillersMiddle,
            _ => FillersOlder,
        };

        var sb = new StringBuilder();

        // Opening sentence anchors the scene to its beat.
        var beat = scene.Summary.Length > 0 ? scene.Summary : "the evening went on gently";
        sb.Append(OpenScene(beat, scene.Id, profile.Band));

        // Slot-filled filler sentences until the word budget for this scene is met.
        var closingReserve = isFinal ? 60 : 0;
        var used = new HashSet<int>();
        while (CountWords(sb) < target - closingReserve)
        {
            int pick;
            do { pick = rng.Next(fillers.Length); } while (used.Count < fillers.Length && !used.Add(pick));
            if (used.Count >= fillers.Length) used.Clear();
            sb.Append(' ').Append(Fill(fillers[pick], slots));
        }

        if (isFinal)
        {
            sb.Append("\n\n").Append(Fill(SleepyClosings[rng.Next(SleepyClosings.Length)], slots));
        }

        // Older bands read as flowing prose: merge adjacent short sentences until the
        // scene clears the founder's >=20-word average (raised from 15, 2026-07-31).
        var sceneText = profile.Band == AgeBand.Age3To4 ? sb.ToString() : FlowJoin(sb.ToString(), rng);
        return Task.FromResult(sceneText);
    }

    /// <summary>Deterministically merges adjacent short sentences with gentle connectors
    /// until the average sentence length reaches ~20.5 words. Common openers are
    /// lowercased after the join; anything else (names) keeps its capital.</summary>
    internal static string FlowJoin(string text, DeterministicRandom rng)
    {
        var safeLower = new HashSet<string>(StringComparer.Ordinal)
        { "The", "It", "She", "He", "They", "Then", "Soon", "A", "An", "Her", "His", "Their", "Everything", "Everyone", "Together" };

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        for (var p = 0; p < paragraphs.Length; p++)
        {
            var parts = paragraphs[p].Split(". ", StringSplitOptions.RemoveEmptyEntries).ToList();
            for (var guard = 0; guard < 24; guard++)
            {
                var words = parts.Sum(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                if (parts.Count <= 1 || (double)words / parts.Count >= 20.5) break;

                // Merge the first adjacent pair whose combined length stays comfortable.
                var merged = false;
                for (var i = 0; i < parts.Count - 1; i++)
                {
                    var a = parts[i]; var b = parts[i + 1];
                    if (a.EndsWith('!') || a.EndsWith('?') || a.EndsWith('…') || b.StartsWith('"') || b.StartsWith('“')) continue;
                    var combined = a.Split(' ').Length + b.Split(' ').Length;
                    if (combined > 40) continue;

                    var firstWord = b.Split(' ', 2)[0];
                    var rest = safeLower.Contains(firstWord)
                        ? char.ToLowerInvariant(b[0]) + b[1..]
                        : b;
                    var connector = rng.Next(3) switch { 0 => ", and ", 1 => " while ", _ => ", and " };
                    parts[i] = a.TrimEnd('.') + connector + rest;
                    parts.RemoveAt(i + 1);
                    merged = true;
                    break;
                }
                if (!merged) break;
            }
            paragraphs[p] = string.Join(". ", parts);
        }
        return string.Join("\n\n", paragraphs);
    }

    public Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Normalize whitespace; guarantee the gentle close (design covenant: no cliffhangers).
        var text = string.Join("\n\n",
            stitchedText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Join(' ', p.Split(' ', StringSplitOptions.RemoveEmptyEntries))));

        if (!text.TrimEnd().EndsWith("Goodnight.", StringComparison.Ordinal))
        {
            text += "\n\nAnd with that, the night grew soft and still. " +
                    $"{profile.Name} closed both eyes, breathed slowly, and drifted into a calm, cozy sleep. Goodnight.";
        }
        return Task.FromResult(text);
    }

    /// <summary>Window-scoped polish (overlapped pipeline): whitespace normalization for
    /// every window, but the gentle goodnight close is appended ONLY on the final window —
    /// a middle window must never end the story.</summary>
    public Task<string> PolishWindowAsync(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isFinalWindow) return PolishAsync(profile, windowText, ct);
        var text = string.Join("\n\n",
            windowText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Join(' ', p.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        return Task.FromResult(text);
    }

    /// <summary>Poster spec contract v3: the deterministic derivation from the outline's
    /// own metadata (title, settings, characters, moral) — the same path every engine falls
    /// back to when no model is available. Same outline → same spec.</summary>
    public Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(StoryTime.Poster.Spec.PosterSpecDeriver.Derive(outline));
    }

    public Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Deterministic gentle-substitution repair. The template engine should never emit
        // flagged text, but the seam must behave like the real engine's bounded self-repair.
        var repaired = passage;
        foreach (var (term, replacement) in GentleSubstitutes)
        {
            repaired = System.Text.RegularExpressions.Regex.Replace(
                repaired, $@"\b{term}\b", replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return Task.FromResult(repaired);
    }

    private static readonly (string Term, string Replacement)[] GentleSubstitutes =
    [
        ("monsters", "shadow puppets"), ("monster", "shadow puppet"),
        ("scary", "surprising"), ("terrifying", "surprising"), ("terrified", "startled"),
        ("nightmares", "muddled dreams"), ("nightmare", "muddled dream"),
        ("fighting", "squabbling gently"), ("fight", "disagreement"),
        ("screamed", "gasped"), ("screams", "gasps"), ("scream", "gasp"),
        ("killed", "chased away"), ("kill", "shoo"),
        ("dead", "fast asleep"), ("death", "the end of the day"),
        ("died", "faded"), ("dying", "fading"), ("die", "fade"),
        ("evil", "grumpy"), ("wicked", "grumpy"), ("cursed", "muddled"), ("curse", "muddle"),
        ("haunted", "sleepy old"), ("ghosts", "moon shadows"), ("ghost", "moon shadow"),
        ("weapons", "walking sticks"), ("weapon", "walking stick"),
        ("swords", "wooden spoons"), ("sword", "wooden spoon"),
        ("guns", "pinecones"), ("gun", "pinecone"),
        ("blood", "berry juice"), ("hurt", "bump"), ("attacked", "surprised"), ("attack", "surprise"),
        ("hated", "did not enjoy"), ("hates", "does not enjoy"), ("hate", "do not enjoy"),
        ("stupid", "silly"), ("idiot", "goose"),
        ("trapped", "tucked"), ("trap", "tuck"),
        ("battle", "pillow toss"), ("war", "long game"),
        ("drowned", "splashed"), ("drowning", "splashing"), ("drown", "splash"),
        ("poisoned", "over-peppered"), ("poison", "pepper"),
    ];

    // ---------- Helpers ----------

    private static Theme PickTheme(ChildProfile profile, DeterministicRandom rng)
    {
        var known = profile.Interests.Where(Themes.ContainsKey).ToList();
        if (known.Count == 0) return DefaultTheme;
        return Themes[known[rng.Next(known.Count)]];
    }

    private static Dictionary<string, string> BuildSlots(string childName, string friendName, Theme theme) => new()
    {
        ["name"] = string.IsNullOrWhiteSpace(childName) ? "Little One" : childName.Trim(),
        ["friend"] = friendName,
        ["creature"] = theme.Creature,
        ["Creature"] = Capitalize(theme.Creature),
        ["place"] = theme.Place,
        ["placeB"] = theme.PlaceB,
        ["thing"] = theme.Thing,
        ["sound"] = theme.Sound,
        ["adj"] = theme.Adj,
    };

    private static string Fill(string template, Dictionary<string, string> slots)
    {
        var result = template;
        foreach (var (key, value) in slots)
            result = result.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return result;
    }

    // ---------- Title composer (shape-diverse, 2026-07-31) ----------

    /// <summary>Curated one-word evocative titles — the subtitle-free shape.</summary>
    private static readonly string[] EvocativeTitles =
        ["Thistledown", "Moonwash", "Lanternlight", "Starhush", "Dreamdrift", "Nightpetal",
         "Willowsong", "Emberglow", "Feathermoon", "Hushabye"];

    private static readonly string[] TitleGentleVerbs = ["Hummed", "Whispered", "Twinkled", "Giggled", "Yawned"];
    private static readonly string[] TitleCarryVerbs = ["Carrying", "Bringing", "Rocking", "Walking"];
    private static readonly string[] TitleShareVerbs = ["Sharing", "Keeping", "Minding", "Finding"];
    private static readonly string[] TitleSoftAdjectives = ["Sleepy", "Moonlit", "Drowsy", "Quiet", "Velvet", "Softest"];

    /// <summary>Eight grammatical title shapes × seeded word pools. One shape is the old
    /// skeleton title, so the classic flavor survives as one voice among eight instead of
    /// the only voice. The similarity guard below redraws when a candidate shares more
    /// than half its content tokens with a recent title.</summary>
    private static readonly Func<DeterministicRandom, Skeleton, Dictionary<string, string>, string>[] TitleShapes =
    [
        // 0 · classic skeleton title ("The Lantern Fox", "A Gift for Pip", …)
        (rng, skeleton, slots) => Fill(skeleton.TitleTemplate, slots),
        // 1 · name + place ("Wren at the Fern Hollow")
        (rng, _, slots) => $"{slots["friend"]} at the {TitleCaseNoArticle(rng.Next(2) == 0 ? slots["place"] : slots["placeB"])}",
        // 2 · "... and the ..." pairing ("Wren and the Copper Lantern")
        (rng, _, slots) => rng.Next(2) == 0
            ? $"{slots["friend"]} and the {TitleCaseNoArticle(slots["thing"])}"
            : $"{slots["name"]} and the {(rng.Next(2) == 0 ? TitleCase(slots["adj"]) : TitleSoftAdjectives[rng.Next(TitleSoftAdjectives.Length)])} {TitleCase(slots["creature"])}",
        // 3 · gentle question ("Who Hummed in the Fern Hollow?")
        (rng, _, slots) => rng.Next(2) == 0
            ? $"Who {TitleGentleVerbs[rng.Next(TitleGentleVerbs.Length)]} in the {TitleCaseNoArticle(slots["place"])}?"
            : $"Where Did the {TitleCase(slots["creature"])} Doze?",
        // 4 · "How ..." ("How the Otter Found the Spiral Shell")
        (rng, _, slots) => rng.Next(2) == 0
            ? $"How the {TitleCase(slots["creature"])} Found the {TitleCaseNoArticle(slots["thing"])}"
            : $"How {slots["friend"]} Learned to Listen",
        // 5 · doing-title ("Carrying the Glimmer Jar Home")
        (rng, _, slots) => rng.Next(3) switch
        {
            0 => $"{TitleCarryVerbs[rng.Next(TitleCarryVerbs.Length)]} the {TitleCaseNoArticle(slots["thing"])} Home",
            1 => $"Counting Stars with {slots["friend"]}",
            _ => $"{TitleShareVerbs[rng.Next(TitleShareVerbs.Length)]} the {TitleCaseNoArticle(slots["thing"])}",
        },
        // 6 · single evocative word, subtitle-free
        (rng, _, _) => EvocativeTitles[rng.Next(EvocativeTitles.Length)],
        // 7 · night turn ("One Sleepy Night in the Tide Pools" / "Goodnight, Tide Pools")
        (rng, _, slots) => rng.Next(2) == 0
            ? $"One Sleepy Night in the {TitleCaseNoArticle(slots["placeB"])}"
            : $"Goodnight, {TitleCaseNoArticle(slots["placeB"])}",
    ];

    /// <summary>Seeded shape pick with a similarity guard: a candidate sharing &gt;50% of
    /// its content tokens (TitleMetrics.TokenJaccard) with any recent title is redrawn on
    /// the next shape, cycling through all shapes before giving up (deterministic).</summary>
    private static string ComposeTitle(DeterministicRandom rng, Skeleton skeleton, Dictionary<string, string> slots,
        IReadOnlyList<string>? recentTitles)
    {
        var start = rng.Next(TitleShapes.Length);
        var candidate = string.Empty;
        for (var attempt = 0; attempt < TitleShapes.Length; attempt++)
        {
            candidate = TitleShapes[(start + attempt) % TitleShapes.Length](rng, skeleton, slots);
            var tooSimilar = recentTitles is { Count: > 0 } &&
                recentTitles.Any(r => TitleMetrics.TokenJaccard(candidate, r) > 0.5);
            if (!tooSimilar) return candidate;
        }
        return candidate;
    }

    /// <summary>"the fern hollow" → "Fern Hollow" (drops a leading article, Title-Cases).</summary>
    private static string TitleCaseNoArticle(string phrase)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "the" or "a" or "an") words.RemoveAt(0);
        return string.Join(' ', words.Select(Capitalize));
    }

    private static string TitleCase(string phrase) =>
        string.Join(' ', phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Capitalize));

    private static string OpenScene(string beat, int sceneId, AgeBand band)
    {
        return sceneId == 0
            ? $"Once, on a soft and sleepy evening, {beat.TrimEnd('.')}."
            : band == AgeBand.Age3To4 ? $"Then, {beat.TrimEnd('.')}." : $"Soon after, {beat.TrimEnd('.')}.";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int CountWords(StringBuilder sb) =>
        sb.ToString().Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    public static int DeriveSeed(ChildProfile profile, int episodeCount)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in profile.Name) h = h * 31 + c;
            foreach (var interest in profile.Interests)
                foreach (var c in interest) h = h * 31 + c;
            h = h * 31 + profile.Age;
            h = h * 31 + (int)profile.Length;
            h = h * 31 + episodeCount;
            return h;
        }
    }

    private static int HashCombine(int a, int b)
    {
        unchecked { return a * 486187739 + b; }
    }
}
