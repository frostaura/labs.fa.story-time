using System.Text.Json;

namespace StoryTime.Core.Engines.AppleFoundation;

/// <summary>Async C# mirror of the Swift <c>SEFMEngine</c> shim (src/native/StoryTimeNative,
/// contract in docs/research/foundation-models.md). JSON in, JSON out; every call opens a
/// fresh model session. Cancellation through the token cancels the native request.
/// Failures surface as <see cref="FoundationModelsException"/> carrying an
/// <see cref="FmErrorCode"/>. Implementations: the iOS binding bridge in StoryTime.App,
/// a scripted fake in the tests, and a process-pipe bridge in the macOS lab.</summary>
public interface IFoundationModelsBridge
{
    /// <summary>Current system-model availability (cheap; safe to poll).</summary>
    FoundationModelsAvailability GetAvailability();

    /// <summary>Loads model assets ahead of the first request.</summary>
    void Prewarm(string stage);

    /// <summary>Exact token count, or null when the OS cannot count (pre-26.4).</summary>
    Task<int?> CountTokensAsync(string text, CancellationToken ct);

    /// <summary>Guided outline → generated-content JSON.</summary>
    Task<string> GenerateOutlineAsync(string requestJson, CancellationToken ct);

    /// <summary>Streamed scene prose. <paramref name="onDelta"/> receives appended text in
    /// order; the returned JSON <c>{"text": …}</c> is authoritative.</summary>
    Task<string> StreamSceneAsync(string requestJson, Action<string> onDelta, CancellationToken ct);

    /// <summary>Guided scene digest → <c>{"summary", "newFacts"}</c>.</summary>
    Task<string> DigestSceneAsync(string requestJson, CancellationToken ct);

    /// <summary>Plain single-turn rewrite (polish and repair) → <c>{"text": …}</c>.</summary>
    Task<string> PolishSceneAsync(string requestJson, CancellationToken ct);

    /// <summary>Guided poster choice → <c>{scene, timeOfDay, mood, palette, features}</c> (poster spec contract v3, no seed).</summary>
    Task<string> GeneratePosterAsync(string requestJson, CancellationToken ct);
}

/// <summary>Bridge error codes. Values mirror Swift <c>SEFMErrorCode</c> and must never be
/// renumbered.</summary>
public enum FmErrorCode
{
    Ok = 0,
    UnavailableDevice = 1,
    UnavailableNotEnabled = 2,
    UnavailableModelNotReady = 3,
    UnsupportedLocale = 4,
    ContextExceeded = 10,
    Guardrail = 11,
    Refusal = 12,
    RateLimited = 13,
    Concurrent = 14,
    Timeout = 15,
    Parse = 16,
    AssetsUnavailable = 17,
    Cancelled = 20,
    Unknown = 99,
}

/// <summary>A failed bridge call. <see cref="Explanation"/> is the model's refusal text —
/// parent/diagnostics only, never shown to a child.</summary>
public sealed class FoundationModelsException : Exception
{
    public FoundationModelsException(FmErrorCode code, string message, string? explanation = null, DateTimeOffset? resetDate = null)
        : base($"{code}: {message}")
    {
        Code = code;
        Explanation = explanation;
        ResetDate = resetDate;
    }

    public FmErrorCode Code { get; }
    public string? Explanation { get; }
    public DateTimeOffset? ResetDate { get; }

    /// <summary>Builds the exception from the native code + error payload JSON
    /// <c>{"message", "explanation"?, "resetDate"? (unix seconds)}</c>.</summary>
    public static FoundationModelsException FromNative(int code, string? payloadJson)
    {
        var fmCode = Enum.IsDefined(typeof(FmErrorCode), code) ? (FmErrorCode)code : FmErrorCode.Unknown;
        string message = "native call failed";
        string? explanation = null;
        DateTimeOffset? reset = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? message;
                if (root.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String) explanation = e.GetString();
                if (root.TryGetProperty("resetDate", out var r) && r.ValueKind == JsonValueKind.Number)
                    reset = DateTimeOffset.FromUnixTimeMilliseconds((long)(r.GetDouble() * 1000));
            }
            catch (JsonException)
            {
                message = payloadJson;
            }
        }
        return new FoundationModelsException(fmCode, message, explanation, reset);
    }
}

/// <summary>Why the system model cannot be used right now.</summary>
public enum FmUnavailableReason
{
    None,
    DeviceNotEligible,
    AppleIntelligenceNotEnabled,
    ModelNotReady,
    UnsupportedLocale,
    /// <summary>No native bridge in this host (tests, headless) or an unrecognized reason.</summary>
    Unknown,
}

/// <summary>Parsed <c>availabilityJSON</c>.</summary>
public sealed record FoundationModelsAvailability(
    bool IsAvailable, FmUnavailableReason Reason, int ContextSize, string Variant, bool LocaleSupported)
{
    /// <summary>The design floor: iOS 26 sessions hold 4,096 tokens (TN3193).</summary>
    public const int ContextFloor = 4096;

    /// <summary>Hosts with no native bridge (tests, headless).</summary>
    public static FoundationModelsAvailability NoBridge { get; } =
        new(false, FmUnavailableReason.Unknown, ContextFloor, "none", false);

    public static FoundationModelsAvailability Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var available = root.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;
            var reasonText = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var reason = reasonText switch
            {
                "available" => FmUnavailableReason.None,
                "deviceNotEligible" => FmUnavailableReason.DeviceNotEligible,
                "appleIntelligenceNotEnabled" => FmUnavailableReason.AppleIntelligenceNotEnabled,
                "modelNotReady" => FmUnavailableReason.ModelNotReady,
                "unsupportedLocale" => FmUnavailableReason.UnsupportedLocale,
                _ => FmUnavailableReason.Unknown,
            };
            var context = root.TryGetProperty("contextSize", out var c) && c.TryGetInt32(out var ci) ? ci : ContextFloor;
            var variant = root.TryGetProperty("variant", out var v) ? v.GetString() ?? "unknown" : "unknown";
            var locale = root.TryGetProperty("localeSupported", out var l) && l.ValueKind == JsonValueKind.True;
            return new FoundationModelsAvailability(available && reason == FmUnavailableReason.None, reason, context, variant, locale);
        }
        catch (JsonException)
        {
            return NoBridge;
        }
    }
}
