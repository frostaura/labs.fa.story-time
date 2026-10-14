# StoryTime copy glossary

Founder instruction (2026-10-14): drop the old textile-metaphor phrases for something more elegant and simple, so parents and kids don't get confused. This glossary is binding for every user-visible string: XAML, view-model strings, notifications, accessibility text, onboarding, settings, empty states, errors, the Premium page and Info.plist usage descriptions. It overrides older wording in `mockup-spec.md` and the Claude Design mockups.

## Glossary — one word per concept

| Concept | Say | Never say |
|---|---|---|
| Starting a new story | **Make** — "Make tonight's story" | we·ave, spin, craft, conjure |
| A story being made | **Making** — "Making Aria's story…", eyebrow `MAKING · 43%`, list meta "making…" | we·aving, on the lo·om |
| A finished story | **Ready** — "Aria's story is ready.", "Story ready — play the teaser ▶" | wo·ven |
| The short preview a parent hears first | **Teaser** | sneak peek, trailer |
| The wait between stories | **Next story at {time}** · "30 minutes between stories" | the lo·om rests, cooldown |
| A story that did not finish | **Didn't finish** — "This story didn't finish." | tangled, the lo·om needs a moment |
| The steps of making a story | **Planning · Writing · Picture · Voice** | outline, stitch, polish, scenes |
| The soft sound while a story is made | **Soft hum** | lo·om hum |
| All stories | **Library** ("Every story") | archive |
| The child-safe browse screen | **Kid Shelf** | — |
| The one-off purchase | **Premium** — "Buy Premium", "Restore purchase" | subscription, plan, upgrade tier |
| Anything else | **story** | tale, yarn, thread |

## Tone

- Warm and calm; short words a tired parent reads at a glance.
- Say what happens, not a metaphor for it.
- Name the child where the copy is about their story.
- No exclamation marks; nothing loud, nothing urgent.

The banned words are written with a middle dot (`we·ave`) so a plain search finds nothing: the founder rule (2026-10-14) keeps them out of the product source entirely, not only out of user-visible copy. That covers identifiers, file and type names, routes and QA route names, environment variables, BGTask identifiers, log text, comments, prompts, story-template prose, tests, scripts and these docs. `BannedWordGuardTests` (`tests/StoryTime.Core.Tests`) scans the repository and fails if any of them comes back. The one deliberate exception is `ProseGuards`, which rewrites model output and spells its patterns with bracketed letters (`w[e]ave`); the historical `qa-report.md` and `qa-shots/` are left as recorded.

Internal names follow the glossary too: `MakingSheetViewModel`, `IStoryMakingHost` / `StoryMakingHost`, `SoftHum` / `SoftHumService`, `StoryPipeline.MakeStoryAsync`, the `making` QA route.
