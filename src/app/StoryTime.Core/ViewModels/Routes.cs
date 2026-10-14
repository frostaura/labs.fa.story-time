namespace StoryTime.Core.ViewModels;

/// <summary>Shell route names (architecture §12). None collides with the single root tab
/// route ("home") — a push route starting with a tab route name crashes Shell (MAUI 10.0.60).</summary>
public static class Routes
{
    public const string Home = "//home";
    public const string OnboardingProfile = "onboarding-profile";
    public const string OnboardingVoice = "onboarding-voice";
    public const string OnboardingRules = "onboarding-rules";
    /// <summary>QA-only pseudo-route: no page behind it any more (the Making page became
    /// the making sheet, 2026-07-30). App.OnShellReadyAsync intercepts it and opens Home
    /// with the sheet expanded instead of navigating.</summary>
    public const string Making = "making";
    public const string Player = "player";
    public const string Shelf = "shelf";
    public const string Settings = "settings";
    public const string Library = "library";
    public const string Premium = "premium";
    /// <summary>Pushed from Settings (inside the biometric-gated flow — no extra gate).
    /// Flat name; does not start with the "home" tab route (Shell gotcha).</summary>
    public const string ContentFilters = "content-filters";
}
