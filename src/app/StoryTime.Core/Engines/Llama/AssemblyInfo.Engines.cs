using System.Runtime.CompilerServices;

// Engine internals (LlamaNative, LlamaRunner, LlamaPrompts, GrammarStore) are exercised
// directly by the unit tests in tests/StoryTime.Core.Tests/Engines/.
[assembly: InternalsVisibleTo("StoryTime.Core.Tests")]
