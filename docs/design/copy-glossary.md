# StoryTime copy glossary

Founder instruction (2026-10-14): "Get rid of the Weave phrases. Something more elegant and simple so parents and kids don't get confused." This glossary is binding for every user-visible string: XAML, view-model strings, notifications, accessibility text, onboarding, settings, empty states, errors, the Premium page and Info.plist usage descriptions. It overrides older wording in `mockup-spec.md` and the Claude Design mockups.

## Glossary — one word per concept

| Concept | Say | Never say |
|---|---|---|
| Starting a new story | **Make** — "Make tonight's story" | weave, spin, craft, conjure |
| A story being made | **Making** — "Making Aria's story…", eyebrow `MAKING · 43%`, list meta "making…" | weaving, on the loom |
| A finished story | **Ready** — "Aria's story is ready.", "Story ready — play the teaser ▶" | woven |
| The short preview a parent hears first | **Teaser** | sneak peek, trailer |
| The wait between stories | **Next story at {time}** · "30 minutes between stories" | the loom rests, cooldown |
| A story that did not finish | **Didn't finish** — "This story didn't finish." | tangled, the loom needs a moment |
| The steps of making a story | **Planning · Writing · Picture · Voice** | outline, stitch, polish, scenes |
| The soft sound while a story is made | **Soft hum** | loom hum |
| All stories | **Library** ("Every story") | archive |
| The child-safe browse screen | **Kid Shelf** | — |
| The one-off purchase | **Premium** — "Buy Premium", "Restore purchase" | subscription, plan, upgrade tier |
| Anything else | **story** | tale, yarn, thread |

## Tone

- Warm and calm; short words a tired parent reads at a glance.
- Say what happens, not a metaphor for it.
- Name the child where the copy is about their story.
- No exclamation marks; nothing loud, nothing urgent.

Internal identifiers (`WeaveSheetViewModel`, `IWeaveHost`, `LoomHum`, routes such as `weaving`, log tags) are not user-visible and are renamed in a separate sweep.
