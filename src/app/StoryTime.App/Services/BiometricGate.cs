using StoryTime.Core.Abstractions;

using LocalAuthentication;

namespace StoryTime.App.Services;

/// <summary>Biometric gate for parent settings — prompted on EVERY entry (structural
/// invariant). iOS: LocalAuthentication (Face ID / Touch ID, falling back to the device
/// passcode when biometrics is unavailable or denied). The gate must NEVER throw — any platform
/// error resolves to a graceful false and the settings veil offers "Try again".
/// Info.plist carries NSFaceIDUsageDescription: without it iOS KILLS the app the moment
/// LAContext evaluates Face ID on a real device (the 2026-07-30 founder-reported crash).
/// Debug builds on simulators with no auth mechanism at all auto-pass after a
/// 300 ms delay and report "DEV BYPASS".</summary>
public sealed class BiometricGate : IBiometricGate
{
    private string _mechanismName = "Biometrics";

    public string MechanismName => _mechanismName;

    public async Task<bool> AuthenticateAsync(string reason, CancellationToken ct)
    {
        try
        {
            using var context = new LAContext();

            // Prefer biometrics; fall back to DeviceOwnerAuthentication (passcode) when
            // biometrics is unavailable, unenrolled, locked out, or permission-denied.
            var policy = LAPolicy.DeviceOwnerAuthenticationWithBiometrics;
            if (!context.CanEvaluatePolicy(policy, out _))
            {
                policy = LAPolicy.DeviceOwnerAuthentication;
                if (!context.CanEvaluatePolicy(policy, out _))
                {
#if DEBUG
                    return await DevBypassAsync(ct);
#else
                    _mechanismName = "None";
                    return false;
#endif
                }
            }

            _mechanismName = policy == LAPolicy.DeviceOwnerAuthentication
                ? "Passcode"
                : context.BiometryType switch
                {
                    LABiometryType.FaceId => "Face ID",
                    LABiometryType.TouchId => "Touch ID",
                    _ => "Biometrics",
                };

            var (ok, _) = await context.EvaluatePolicyAsync(policy, reason);
            return ok;
        }
        catch
        {
            // Whatever LocalAuthentication did, the app survives and the gate stays shut.
            _mechanismName = "None";
            return false;
        }
    }

    private async Task<bool> DevBypassAsync(CancellationToken ct)
    {
        await Task.Delay(300, ct);
        _mechanismName = "DEV BYPASS";
        return true;
    }
}
