using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StoryTime.App.Platforms.iOS;

/// <summary>
/// llama ships on iOS as a DYNAMIC llama.framework embedded in the app bundle
/// (NativeReference in Native.props; upstream build-xcframework.sh emits dynamic slices).
/// Core's DllImport("llama") declarations stay portable because this resolver maps the
/// "llama" library name to Frameworks/llama.framework/llama at runtime, falling back to
/// the main program handle should the linkage ever become static.
/// Registered automatically via ModuleInitializer — no MauiProgram wiring required.
/// </summary>
public static class LlamaNativeResolver
{
    private static bool _registered;

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        NativeLibrary.SetDllImportResolver(typeof(StoryTime.Core.Engines.LlamaStoryEngine).Assembly,
            static (name, _, _) =>
            {
                if (name != "llama") return IntPtr.Zero;
                var framework = Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Frameworks", "llama.framework", "llama");
                return File.Exists(framework) && NativeLibrary.TryLoad(framework, out var handle)
                    ? handle
                    : NativeLibrary.GetMainProgramHandle();
            });
    }
}
