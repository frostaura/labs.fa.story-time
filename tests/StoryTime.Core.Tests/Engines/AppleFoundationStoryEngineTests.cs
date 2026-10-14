using System.Text.Json;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Engines;
using StoryTime.Core.Engines.AppleFoundation;
using StoryTime.Core.Pipeline;
using StoryTime.Core.Poster;
using StoryTime.Core.Engines.Prompts;
using StoryTime.Poster.Spec;
using StoryTime.Core.Services;
using Xunit;

namespace StoryTime.Core.Tests.Engines;

/// <summary>Deterministic scripted bridge: each op answers from a queue of scripted steps
/// (text or exception) and falls back to a well-formed default. Every request is recorded.</summary>
internal sealed class FakeBridge : IFoundationModelsBridge
{
    public FoundationModelsAvailability Availability { get; set; } =
        new(true, FmUnavailableReason.None, 4096, "test", true);

    public List<(string Op, FmRequest Request)> Calls { get; } = [];
    public Dictionary<string, Queue<Func<FmRequest, string>>> Script { get; } = new();
    public int? TokenCount { get; set; }
    public int PrewarmCount { get; private set; }

    public void Enqueue(string op, Func<FmRequest, string> step)
    {
        if (!Script.TryGetValue(op, out var q)) Script[op] = q = new Queue<Func<FmRequest, string>>();
        q.Enqueue(step);
    }

    public void EnqueueError(string op, FmErrorCode code, string? explanation = null, DateTimeOffset? reset = null) =>
        Enqueue(op, _ => throw new FoundationModelsException(code, "scripted", explanation, reset));

    public void EnqueueText(string op, string text) =>
        Enqueue(op, _ => JsonSerializer.Serialize(new { text }));

    public IEnumerable<FmRequest> Requests(string op) => Calls.Where(c => c.Op == op).Select(c => c.Request);

    public FoundationModelsAvailability GetAvailability() => Availability;
    public void Prewarm(string stage) => PrewarmCount++;
    public Task<int?> CountTokensAsync(string text, CancellationToken ct) => Task.FromResult(TokenCount);

    private Task<string> Answer(string op, string json, CancellationToken ct, Func<FmRequest, string> fallback)
    {
        ct.ThrowIfCancellationRequested();
        var request = JsonSerializer.Deserialize<FmRequest>(json, FmJson.Options)!;
        Calls.Add((op, request));
        var step = Script.TryGetValue(op, out var q) && q.Count > 0 ? q.Dequeue() : fallback;
        return Task.FromResult(step(request));
    }

    public Task<string> GenerateOutlineAsync(string requestJson, CancellationToken ct) =>
        Answer("outline", requestJson, ct, r => OutlineJson(r.SceneCount ?? 5));

    public async Task<string> StreamSceneAsync(string requestJson, Action<string> onDelta, CancellationToken ct)
    {
        var json = await Answer("scene", requestJson, ct, _ => JsonSerializer.Serialize(new { text = Prose(Calls.Count) }));
        var text = JsonSerializer.Deserialize<FmText>(json, FmJson.Options)!.Text;
        foreach (var chunk in text.Chunk(40)) onDelta(new string(chunk));
        ct.ThrowIfCancellationRequested();
        return json;
    }

    public Task<string> DigestSceneAsync(string requestJson, CancellationToken ct) =>
        Answer("digest", requestJson, ct, _ => JsonSerializer.Serialize(new
        {
            summary = $"Digest number {Calls.Count} happened gently.",
            newFacts = new[] { $"fact {Calls.Count}" },
        }));

    public Task<string> PolishSceneAsync(string requestJson, CancellationToken ct) =>
        Answer("polish", requestJson, ct, r =>
        {
            // Echo the passage after the prompt's blank line, lightly changed.
            var passage = r.Prompt[(r.Prompt.LastIndexOf("\n\n", StringComparison.Ordinal) + 2)..];
            return JsonSerializer.Serialize(new { text = passage.Replace("softly", "gently") });
        });

    public Task<string> GeneratePosterAsync(string requestJson, CancellationToken ct) =>
        Answer("poster", requestJson, ct, _ => JsonSerializer.Serialize(new
        {
            scene = "MoonlitSea", timeOfDay = "Moonlit", mood = "Dreamy", palette = "OceanDeep", features = new[] { "Sailboat", "Otter" },
        }));

    public static string OutlineJson(int scenes, string title = "Pip and the Silver Shell", string summary = "Pip and Mila walk softly along the shore")
    {
        return JsonSerializer.Serialize(new
        {
            plan = "a calm walk that ends in sleep",
            title,
            settings = new[] { "the silver shore" },
            characters = new[] { new { name = "Pip", kind = "Otter", trait = "Gentle" }, new { name = "Mila", kind = "girl", trait = "curious" } },
            scenes = Enumerable.Range(0, scenes).Select(i => new { summary = $"{summary} part {i + 1}", beats = new[] { "a shell", "a song" } }),
            theme = "kindness makes the night warm",
            bible = new[] { "Pip carries a silver shell" },
        });
    }

    private static readonly string[] Pool =
    [
        "The sand felt cool and smooth under his paws as the tide whispered along the beach.",
        "Mila laughed quietly when a little crab waved hello from its rocky pool.",
        "They shared a warm biscuit and listened to the sea breathe slowly in and out.",
        "A ribbon of moonlight painted a silver path across the calm dark water.",
        "Seagulls tucked their heads under their wings on the old wooden pier.",
        "The lighthouse blinked its kind golden eye far across the sleepy bay.",
        "Pip found a smooth pebble striped like a sunset and gave it to Mila.",
        "Soft foam curled around their toes and slipped back into the waves again.",
        "A gentle breeze carried the smell of salt and sun-warmed seaweed past them.",
        "Mila hummed a slow tune while Pip tapped the rhythm on a driftwood log.",
        "Tiny shells glittered along the waterline like a necklace left for the night.",
        "The stars came out one by one above the quiet beach and the rolling dunes.",
    ];

    /// <summary>Distinct, clean, multi-sentence prose per call index: a fixed opening plus
    /// three pool sentences that never overlap between nearby indices (so the repetition
    /// guard has nothing to strip).</summary>
    public static string Prose(int n) =>
        $"Pip padded along the shore number {n} while the waves hummed softly beside him. " +
        string.Join(" ", Enumerable.Range(0, 3).Select(k => Pool[(3 * n + k) % Pool.Length]));
}

public class AppleFoundationStoryEngineTests
{
    private static ChildProfile Profile(StoryLength length = StoryLength.Five) => new()
    {
        Id = 1, Name = "Mila", Age = 5, Interests = ["Ocean", "Stars"], Length = length, Mode = StoryMode.OneShot,
    };

    private static (AppleFoundationStoryEngine Engine, FakeBridge Bridge, List<TimeSpan> Waits) Create(AppleFoundationOptions? options = null)
    {
        var bridge = new FakeBridge();
        var waits = new List<TimeSpan>();
        var engine = new AppleFoundationStoryEngine(bridge, new SafetyFilter(), () => 777, options,
            delay: (span, _) => { waits.Add(span); return Task.CompletedTask; });
        return (engine, bridge, waits);
    }

    private static async Task<(StoryOutline Outline, List<string> Scenes)> RunScenesAsync(AppleFoundationStoryEngine engine, ChildProfile profile)
    {
        var outline = await engine.GenerateOutlineAsync(profile, null, TemplateStoryEngine.SceneCount(profile.Length), CancellationToken.None);
        var scenes = new List<string>();
        foreach (var scene in outline.Scenes)
        {
            var tail = scenes.Count > 0 ? StoryPipeline.SplitSentences(scenes[^1])[^1] : string.Empty;
            scenes.Add(await engine.GenerateSceneAsync(profile, outline, scene, tail, CancellationToken.None));
        }
        return (outline, scenes);
    }

    // ---------------- happy path ----------------

    [Fact]
    public async Task Outline_maps_guided_result_with_child_first_zero_based_scenes_and_seed()
    {
        var (engine, bridge, _) = Create();
        var outline = await engine.GenerateOutlineAsync(Profile(), null, 5, CancellationToken.None);

        Assert.Equal("Pip and the Silver Shell", outline.Title);
        Assert.Equal(777, outline.Seed);
        Assert.Equal(Enumerable.Range(0, 5), outline.Scenes.Select(s => s.Id));
        Assert.Equal("child", outline.Characters[0].Kind);
        Assert.Equal("Mila", outline.Characters[0].Name);
        // The model's duplicate of the child is dropped; companion kinds are normalized.
        Assert.Equal(["Mila", "Pip"], outline.Characters.Select(c => c.Name));
        Assert.Equal("otter", outline.Characters[1].Kind);

        var request = Assert.Single(bridge.Requests("outline"));
        Assert.Equal(5, request.SceneCount);
        Assert.Equal(0.7, request.Options.Temperature);
        Assert.Equal("threshold", request.Options.Sampling);
        Assert.StartsWith("You are StoryTime, a gentle bedtime storyteller for a child aged 5 to 6", request.Instructions);
    }

    [Fact]
    public async Task Each_scene_uses_a_fresh_request_with_running_digest_summary_and_last_two_sentences()
    {
        var (engine, bridge, _) = Create();
        var (_, scenes) = await RunScenesAsync(engine, Profile());

        Assert.Equal(5, scenes.Count);
        Assert.Equal(5, bridge.Requests("scene").Count());
        Assert.Equal(5, bridge.Requests("digest").Count());

        var second = bridge.Requests("scene").ElementAt(1).Prompt;
        Assert.Contains("The story so far: Digest number", second);
        // Last two sentences of scene 1 are carried as labelled context, not to be repeated.
        var lastTwo = string.Join(" ", StoryPipeline.SplitSentences(scenes[0]).TakeLast(2));
        Assert.Contains($"For context only, the previous part ended: \"{lastTwo}\"", second);
        Assert.Contains("Pip carries a silver shell", second);           // story bible
        Assert.Contains("Pip the gentle otter", second);                  // character card
        Assert.Contains("Pip and Mila walk softly along the shore part 2", second); // the beat
    }

    [Fact]
    public async Task Poster_choice_maps_into_a_normalized_v3_spec_with_the_story_seed()
    {
        var (engine, bridge, _) = Create();
        var outline = await engine.GenerateOutlineAsync(Profile(), null, 5, CancellationToken.None);
        var spec = await engine.GeneratePosterSpecAsync(outline, CancellationToken.None);

        Assert.Equal(PosterSpec.CurrentVersion, spec.Version);
        Assert.Equal(PosterScene.MoonlitSea, spec.Scene);
        Assert.Equal(PosterTimeOfDay.Moonlit, spec.TimeOfDay);
        Assert.Equal(PosterMood.Dreamy, spec.Mood);
        Assert.Equal(PosterPalette.OceanDeep, spec.Palette);
        Assert.Equal([PosterFeature.Sailboat, PosterFeature.Otter], spec.Features);
        Assert.Equal(PosterSpecMapper.SeedFor(outline), spec.Seed);
        Assert.Equal(PosterSpecDeriver.Derive(outline).Seed, spec.Seed); // same seed as the template path
        Assert.True(PosterSpecRules.SpecEquals(spec, PosterSpecRules.Normalize(spec)));

        var prompt = bridge.Requests("poster").Single().Prompt;
        Assert.Contains("MoonlitSea", prompt);
        Assert.Contains("Pip and the Silver Shell", prompt);
    }

    [Fact]
    public void Poster_mapper_repairs_unknown_names_and_drops_features_that_do_not_fit_the_scene()
    {
        var spec = PosterSpecMapper.Map(new FmPosterChoice
        {
            Scene = "DesertOasis", TimeOfDay = "Noon", Mood = "", Palette = "NotAPalette",
            Features = ["whale", "Camel", "Fox", "camel", "Unicorn", "Telescope", "Fireflies"],
        }, 5);
        Assert.Equal(PosterScene.DesertOasis, spec.Scene);
        Assert.Equal(PosterTimeOfDay.Moonlit, spec.TimeOfDay);
        Assert.Equal(PosterMood.Calm, spec.Mood);
        Assert.Equal(PosterSpecRules.DefaultPalette(PosterScene.DesertOasis), spec.Palette);
        Assert.Equal([PosterFeature.Camel, PosterFeature.Telescope, PosterFeature.Fireflies], spec.Features);
        Assert.Equal(5, spec.Seed);
    }

    [Fact]
    public void Poster_mapper_refuses_numeric_names_and_tolerates_null_features()
    {
        var spec = PosterSpecMapper.Map(new FmPosterChoice { Scene = "3", Palette = "SnowHush", Features = null! }, 9);
        Assert.Equal(PosterSpecRules.SceneForPalette(PosterPalette.SnowHush), spec.Scene);
        Assert.Empty(spec.Features);
    }

    [Fact]
    public void Poster_prompt_names_every_v3_enum_and_offers_only_compatible_features_per_scene()
    {
        var prompt = StoryPrompts.PosterUser(new StoryOutline { Title = "Moss and the Moon", Settings = ["a snowy village"] });
        foreach (var name in Enum.GetNames<PosterScene>().Concat(Enum.GetNames<PosterTimeOfDay>())
                     .Concat(Enum.GetNames<PosterMood>()).Concat(Enum.GetNames<PosterPalette>()).Concat(Enum.GetNames<PosterFeature>()))
            Assert.Contains(name, prompt);
        var reefLine = prompt.Split('\n').Single(l => l.StartsWith("ReefAtDusk:", StringComparison.Ordinal));
        Assert.Contains("Fish", reefLine);
        Assert.DoesNotContain("Lighthouse", reefLine);
        Assert.DoesNotContain("layers", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- whole-story fallbacks ----------------

    [Fact]
    public async Task Unavailable_model_renders_the_whole_story_from_templates_without_calling_the_model()
    {
        var (engine, bridge, _) = Create();
        bridge.Availability = new FoundationModelsAvailability(false, FmUnavailableReason.AppleIntelligenceNotEnabled, 4096, "x", true);
        var profile = Profile();
        var (outline, scenes) = await RunScenesAsync(engine, profile);

        var template = new TemplateStoryEngine(() => 777);
        var expected = await template.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        Assert.Equal(expected.Title, outline.Title);
        Assert.Equal(await template.GenerateSceneAsync(profile, expected, expected.Scenes[0], string.Empty, CancellationToken.None), scenes[0]);
        Assert.Empty(bridge.Calls);
    }

    [Theory]
    [InlineData(FmErrorCode.AssetsUnavailable)]
    [InlineData(FmErrorCode.UnsupportedLocale)]
    [InlineData(FmErrorCode.UnavailableModelNotReady)]
    public async Task Unavailability_mid_story_switches_the_rest_of_the_story_to_templates(FmErrorCode code)
    {
        var (engine, bridge, _) = Create();
        bridge.Enqueue("scene", _ => JsonSerializer.Serialize(new { text = FakeBridge.Prose(1) }));
        bridge.EnqueueError("scene", code);
        var (_, scenes) = await RunScenesAsync(engine, Profile());

        Assert.Equal(2, bridge.Requests("scene").Count());   // no model calls after the switch
        Assert.Equal(5, scenes.Count);
        Assert.StartsWith("Pip padded along the shore", scenes[0]);
        Assert.DoesNotContain("Pip padded", scenes[1]);
    }

    [Fact]
    public async Task Outline_failure_falls_back_to_a_template_outline_and_template_story()
    {
        var (engine, bridge, _) = Create();
        for (var i = 0; i < 3; i++) bridge.EnqueueError("outline", FmErrorCode.Guardrail);
        var (_, scenes) = await RunScenesAsync(engine, Profile());

        Assert.Equal(3, bridge.Requests("outline").Count()); // normal, re-run, safer (no permissive for guided)
        Assert.Empty(bridge.Requests("scene"));
        Assert.Equal(5, scenes.Count);
    }

    // ---------------- per-scene ladder ----------------

    [Fact]
    public async Task Guardrail_ladder_is_rerun_then_permissive_then_safer_then_template()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        for (var i = 0; i < 4; i++) bridge.EnqueueError("scene", FmErrorCode.Guardrail);

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        var calls = bridge.Requests("scene").ToList();
        Assert.Equal(4, calls.Count);
        Assert.Null(calls[0].Guardrails);
        Assert.Null(calls[1].Guardrails);
        Assert.NotEqual(calls[0].Options.Seed, calls[1].Options.Seed);       // a re-run varies the seed
        Assert.Equal("permissive", calls[2].Guardrails);
        Assert.Equal(calls[0].Prompt, calls[2].Prompt);                       // same prompt, permissive
        Assert.Null(calls[3].Guardrails);
        Assert.Equal(0.5, calls[3].Options.Temperature);                      // safer phrasing
        Assert.StartsWith("Write a calm, cozy part of a bedtime story", calls[3].Prompt);
        Assert.Equal(await new TemplateStoryEngine().GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None), text);
        Assert.Empty(bridge.Requests("digest"));                              // template scenes are not digested
    }

    [Fact]
    public async Task Permissive_output_is_still_lexicon_filtered()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("scene", FmErrorCode.Guardrail);
        bridge.EnqueueError("scene", FmErrorCode.Guardrail);
        bridge.EnqueueText("scene", FakeBridge.Prose(9) + " Then a monster attacked them with a sword.");

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.True(new SafetyFilter().IsClean(text));
        Assert.DoesNotContain("monster", text);
    }

    [Fact]
    public async Task Refusal_explanation_is_logged_for_diagnostics_and_the_ladder_continues()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("scene", FmErrorCode.Refusal, explanation: "I would rather not write that.");

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.StartsWith("Pip padded", text);
        Assert.Contains(engine.RecentEvents, e => e.Code == FmErrorCode.Refusal && e.Detail == "I would rather not write that.");
    }

    [Fact]
    public async Task Lexicon_hit_on_output_allows_at_most_two_model_attempts_then_template()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueText("scene", FakeBridge.Prose(1) + " The ghost screamed in the haunted house.");
        bridge.EnqueueText("scene", FakeBridge.Prose(2) + " Everyone was terrified of the monster.");

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.Equal(2, bridge.Requests("scene").Count());
        Assert.True(new SafetyFilter().IsClean(text));
        Assert.DoesNotContain("Pip padded", text);
    }

    [Fact]
    public async Task Refusal_text_in_string_output_is_treated_as_a_guardrail_hit()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueText("scene", "I'm sorry, but I can't help with that request. " + FakeBridge.Prose(3));

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.Equal(2, bridge.Requests("scene").Count());
        Assert.StartsWith("Pip padded", text);
    }

    [Fact]
    public async Task Context_overflow_trims_summary_and_notes_and_retries_once_keeping_the_beat()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var (outline, _) = await RunScenesAsync(engine, profile);
        bridge.EnqueueError("scene", FmErrorCode.ContextExceeded);

        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[4], "Tail.", CancellationToken.None);

        var last = bridge.Requests("scene").TakeLast(2).ToList();
        Assert.True(last[1].Prompt.Length < last[0].Prompt.Length);
        Assert.Contains("Pip and Mila walk softly along the shore part 5", last[1].Prompt);
    }

    [Fact]
    public async Task Second_context_overflow_falls_back_to_the_template_scene()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("scene", FmErrorCode.ContextExceeded);
        bridge.EnqueueError("scene", FmErrorCode.ContextExceeded);

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.Equal(2, bridge.Requests("scene").Count());
        Assert.DoesNotContain("Pip padded", text);
    }

    [Fact]
    public async Task Rate_limit_waits_until_reset_date_then_retries_up_to_twice()
    {
        var (engine, bridge, waits) = Create(new AppleFoundationOptions { DefaultRetryWait = TimeSpan.FromSeconds(3) });
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("scene", FmErrorCode.RateLimited, reset: DateTimeOffset.UtcNow.AddSeconds(20));
        bridge.EnqueueError("scene", FmErrorCode.Timeout);

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.StartsWith("Pip padded", text);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 15, 20);
        Assert.Equal(TimeSpan.FromSeconds(3), waits[1]);
    }

    [Fact]
    public async Task Third_rate_limit_falls_back_to_the_template_scene()
    {
        var (engine, bridge, waits) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        for (var i = 0; i < 3; i++) bridge.EnqueueError("scene", FmErrorCode.RateLimited);

        var text = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);

        Assert.Equal(2, waits.Count);
        Assert.DoesNotContain("Pip padded", text);
    }

    [Fact]
    public async Task More_than_two_fallen_back_scenes_switch_the_rest_of_the_story_to_templates()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        // Scenes 0..2 each exhaust the guardrail ladder (4 attempts each).
        for (var i = 0; i < 12; i++) bridge.EnqueueError("scene", FmErrorCode.Guardrail);

        var scenes = new List<string>();
        foreach (var scene in outline.Scenes)
            scenes.Add(await engine.GenerateSceneAsync(profile, outline, scene, string.Empty, CancellationToken.None));

        Assert.Equal(12, bridge.Requests("scene").Count()); // scenes 3 and 4 never reach the model
        Assert.All(scenes, s => Assert.DoesNotContain("Pip padded", s));
    }

    [Fact]
    public async Task Repeating_the_previous_tail_is_stripped_from_the_new_scene()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        var first = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);
        var lastTwo = string.Join(" ", StoryPipeline.SplitSentences(first).TakeLast(2));
        bridge.EnqueueText("scene", lastTwo + " " + FakeBridge.Prose(51) + " " + FakeBridge.Prose(53));

        var second = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[1], string.Empty, CancellationToken.None);

        Assert.DoesNotContain(lastTwo, second);
        Assert.StartsWith("Pip padded along the shore number 51", second);
    }

    [Fact]
    public async Task Stock_lines_already_used_in_the_story_are_removed_and_listed_as_do_not_reuse()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueText("scene", FakeBridge.Prose(1) + " Everything feels just right.");
        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);
        bridge.EnqueueText("scene", FakeBridge.Prose(2) + " Everything feels just right. " + FakeBridge.Prose(3));

        var second = await engine.GenerateSceneAsync(profile, outline, outline.Scenes[1], string.Empty, CancellationToken.None);

        Assert.DoesNotContain("Everything feels just right.", second);
        Assert.Contains("Do not reuse these lines: \"Everything feels just right.\"", bridge.Requests("scene").Last().Prompt);
    }

    [Fact]
    public void Run_on_sentences_are_split_at_clause_joints_without_losing_words()
    {
        var runOn = "Mila walks barefoot along the soft sand where each grain tickles her toes like a secret whispered by the wind, " +
                    "and the ocean hums a quiet song as gentle waves curl around her ankles and leave tiny foam kisses behind, " +
                    "while a little ripple dances beneath the surface and sends small rings of wonder through the calm water, " +
                    "and Finn the fox peeks out from behind a mossy stone with his soft ears twitching at every footstep.";
        var split = ProseGuards.SplitRunOns(runOn);
        var sentences = StoryPipeline.SplitSentences(split);

        Assert.True(sentences.Count >= 3);
        Assert.All(sentences, s => Assert.True(s.Split(' ').Length <= 45));
        Assert.StartsWith("The ocean hums", sentences[1]);
        static string Words(string t) => string.Join(' ', t.Replace(",", "").Replace(".", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("and" or "while" or "And" or "While")).Select(w => w.ToLowerInvariant()));
        Assert.Equal(Words(runOn), Words(split));
    }

    [Fact]
    public async Task Outline_beats_are_reframed_positively_before_scene_prompts()
    {
        var (engine, bridge, _) = Create();
        bridge.Enqueue("outline", r => FakeBridge.OutlineJson(r.SceneCount ?? 5, summary: "Pip is afraid of the dark"));
        var outline = await engine.GenerateOutlineAsync(Profile(), null, 5, CancellationToken.None);

        Assert.All(outline.Scenes, s => Assert.StartsWith("Pip is curious about the night", s.Summary));
    }

    [Fact]
    public async Task Discarded_digest_falls_back_to_the_outline_summary()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("digest", FmErrorCode.Guardrail);
        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);
        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[1], string.Empty, CancellationToken.None);

        Assert.Contains("The story so far: Pip and Mila walk softly along the shore part 1.", bridge.Requests("scene").Last().Prompt);
    }

    [Fact]
    public async Task Token_budget_guard_trims_an_oversized_prompt_but_keeps_the_beat()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        // A beat long enough that the estimate asks for an exact count, which "overflows".
        var longBeat = string.Join(" ", Enumerable.Repeat("Pip and Mila walk softly along the shore", 250));
        bridge.Enqueue("outline", r => FakeBridge.OutlineJson(r.SceneCount ?? 5, summary: longBeat));
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, CancellationToken.None);
        bridge.TokenCount = 100_000;
        await engine.GenerateSceneAsync(profile, outline, outline.Scenes[1], "Earlier tail.", CancellationToken.None);

        var prompt = bridge.Requests("scene").Last().Prompt;
        Assert.DoesNotContain("Story notes:", prompt);
        Assert.DoesNotContain("The story so far:", prompt);
        Assert.Contains(longBeat + " part 2", prompt);
    }

    [Fact]
    public async Task Cancellation_propagates_as_operation_cancelled()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var outline = await engine.GenerateOutlineAsync(profile, null, 5, CancellationToken.None);
        bridge.EnqueueError("scene", FmErrorCode.Cancelled);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.GenerateSceneAsync(profile, outline, outline.Scenes[0], string.Empty, cts.Token));
    }

    // ---------------- polish / repair ----------------

    [Fact]
    public async Task Polish_is_per_scene_never_whole_story_and_keeps_text_when_rejected()
    {
        var (engine, bridge, _) = Create(new AppleFoundationOptions { PolishScenes = true });
        var profile = Profile();
        var (_, scenes) = await RunScenesAsync(engine, profile);
        bridge.EnqueueError("polish", FmErrorCode.RateLimited);              // scene 1: kept as written
        bridge.EnqueueText("polish", "Too short.");                          // scene 2: ratio fails → retry
        bridge.EnqueueText("polish", "Still too short.");                    //          → kept as written

        var polished = await engine.PolishAsync(profile, string.Join("\n\n", scenes), CancellationToken.None);

        Assert.Equal(scenes.Count + 1, bridge.Requests("polish").Count());
        Assert.All(bridge.Requests("polish"), r => Assert.True(r.Prompt.Length < scenes[0].Length + 3000));
        Assert.StartsWith(scenes[0], polished);
        Assert.Contains(scenes[1], polished);
        Assert.Contains(scenes[2].Replace("softly", "gently"), polished);
    }

    [Fact]
    public async Task Polish_disabled_by_default_and_final_window_always_closes_gently()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        var (_, scenes) = await RunScenesAsync(engine, profile);

        var middle = await engine.PolishWindowAsync(profile, scenes[0], string.Empty, isFinalWindow: false, CancellationToken.None);
        var final = await engine.PolishWindowAsync(profile, scenes[4], string.Empty, isFinalWindow: true, CancellationToken.None);

        Assert.Empty(bridge.Requests("polish"));
        Assert.Equal(scenes[0], middle);
        Assert.EndsWith("Goodnight.", final);
    }

    [Fact]
    public async Task Repair_uses_the_model_and_falls_back_to_deterministic_repair_when_still_flagged()
    {
        var (engine, bridge, _) = Create();
        var profile = Profile();
        await RunScenesAsync(engine, profile);
        bridge.EnqueueText("polish", "The friends played a gentle game by the sea.");
        bridge.EnqueueText("polish", "The monster was still there.");

        var good = await engine.RepairPassageAsync(profile, "The friends had a fight by the sea.", "fight", CancellationToken.None);
        var fallback = await engine.RepairPassageAsync(profile, "A monster sat by the sea.", "monster", CancellationToken.None);

        Assert.Equal("The friends played a gentle game by the sea.", good);
        Assert.Equal("A shadow puppet sat by the sea.", fallback);
    }

    // ---------------- prompts / inputs ----------------

    [Fact]
    public void Profile_inputs_are_sanitized_before_reaching_a_prompt()
    {
        var profile = new ChildProfile { Name = "Mila<script>\n{}", Age = 6, Interests = ["Ocean", "kill everyone", "Stars!!", "", "a-very-very-long-interest-name-here"] };
        Assert.Equal("Milascript", FoundationPrompts.SafeName(profile));
        Assert.Equal(["Ocean", "Stars"], FoundationPrompts.SafeInterests(profile, new SafetyFilter()));
    }

    [Theory]
    [InlineData("I'm sorry, but I can't write that.", true)]
    [InlineData("As an AI language model, I cannot.", true)]
    [InlineData("Pip said he was sorry for being late, and Mila hugged him.", false)]
    public void Refusal_detection(string text, bool refusal) =>
        Assert.Equal(refusal, AppleFoundationStoryEngine.LooksLikeRefusal(text));

    [Fact]
    public void House_vocabulary_replaces_banned_words_in_model_output()
    {
        var cleaned = AppleFoundationStoryEngine.CleanProse("Their voices were weaving a song. Weave it again, and the loom hums while the hill loomed.");
        Assert.Equal("Their voices were spinning a song. Spin it again, and the spinning wheel hums while the hill rose.", cleaned);
        Assert.DoesNotMatch(@"(?i)weav|wove|loom", cleaned);
    }

    [Fact]
    public void Clean_prose_strips_headings_markdown_and_a_truncated_tail()
    {
        var cleaned = AppleFoundationStoryEngine.CleanProse("# Scene 1\n**Pip** walked home. The moon was round and bright tonight. The stars were wat");
        Assert.Equal("Pip walked home. The moon was round and bright tonight.", cleaned);
    }

    // ---------------- through the real pipeline ----------------

    [Fact]
    public async Task Full_pipeline_makes_a_ready_story_on_the_apple_engine()
    {
        await using var env = await TestEnv.CreateAsync();
        var bridge = new FakeBridge();
        var engine = new AppleFoundationStoryEngine(bridge, new SafetyFilter(), () => 99);
        var settings = new InMemorySettingsStore { ApprovalStep = false };
        var clock = new FakeClock();
        var pipeline = new StoryPipeline(engine, new FakeTtsEngine(), new PosterComposer(), new NullPosterRasterizer(),
            env.Store, settings, new CooldownService(settings, clock, new StoreEntitlements(settings, clock)),
            new SafetyFilter(), new FakeNotificationService(), env.Options, clock);
        var profile = TestEnv.Profile(StoryMode.OneShot);
        await env.Store.SaveProfileAsync(profile);

        var story = await pipeline.WeaveAsync(profile, null, CancellationToken.None);

        Assert.Equal(StoryState.Ready, story.State);
        Assert.Equal("Pip and the Silver Shell", story.Title);
        Assert.Contains("Pip padded along the shore", story.FullText);
        Assert.Single(bridge.Requests("poster"));
    }

    [Fact]
    public void Engine_status_shows_guidance_only_when_the_parent_can_act()
    {
        var state = new FoundationModelsAvailability(false, FmUnavailableReason.AppleIntelligenceNotEnabled, 4096, "x", true);
        var status = new StoryEngineStatus(() => state);
        Assert.True(status.ShowGuidance);
        Assert.Equal(StoryEngineStatus.NotEnabledText, status.GuidanceText);

        state = state with { Reason = FmUnavailableReason.DeviceNotEligible };
        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        status.Refresh();
        Assert.False(status.ShowGuidance);
        Assert.Contains(nameof(StoryEngineStatus.ShowGuidance), changed);

        state = state with { Reason = FmUnavailableReason.ModelNotReady };
        status.Refresh();
        Assert.Equal(StoryEngineStatus.ModelNotReadyText, status.GuidanceText);
    }

    [Fact]
    public void Availability_json_parses_and_bridge_errors_carry_explanation_and_reset_date()
    {
        var a = FoundationModelsAvailability.Parse("{\"available\":true,\"reason\":\"available\",\"contextSize\":8192,\"variant\":\"AFM\",\"localeSupported\":true}");
        Assert.True(a.IsAvailable);
        Assert.Equal(8192, a.ContextSize);
        Assert.Equal(FmUnavailableReason.AppleIntelligenceNotEnabled,
            FoundationModelsAvailability.Parse("{\"available\":false,\"reason\":\"appleIntelligenceNotEnabled\"}").Reason);

        var ex = FoundationModelsException.FromNative(13, "{\"message\":\"slow down\",\"explanation\":\"busy\",\"resetDate\":1800000000}");
        Assert.Equal(FmErrorCode.RateLimited, ex.Code);
        Assert.Equal("busy", ex.Explanation);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), ex.ResetDate);
        Assert.Equal(FmErrorCode.Unknown, FoundationModelsException.FromNative(1234, null).Code);
    }
}
