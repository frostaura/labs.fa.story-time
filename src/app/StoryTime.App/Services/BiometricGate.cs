using StoryTime.Core.Abstractions;

#if IOS
using LocalAuthentication;
#endif
#if ANDROID
using AndroidX.Biometric;
using AndroidX.Core.Content;
#endif

namespace StoryTime.App.Services;

/// <summary>Biometric gate for parent settings — prompted on EVERY entry (structural
/// invariant). iOS: LocalAuthentication (Face ID / Touch ID, falling back to the device
/// passcode when biometrics is unavailable or denied). Android: AndroidX BiometricPrompt
/// (falling back to the device credential). The gate must NEVER throw — any platform
/// error resolves to a graceful false and the settings veil offers "Try again".
/// Info.plist carries NSFaceIDUsageDescription: without it iOS KILLS the app the moment
/// LAContext evaluates Face ID on a real device (the 2026-07-30 founder-reported crash).
/// Debug builds on simulators/emulators with no auth mechanism at all auto-pass after a
/// 300 ms delay and report "DEV BYPASS".</summary>
public sealed class BiometricGate : IBiometricGate
{
    private string _mechanismName = "Biometrics";

    public string MechanismName => _mechanismName;

#if IOS
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
#elif ANDROID
    public async Task<bool> AuthenticateAsync(string reason, CancellationToken ct)
    {
        try
        {
            var activity = Platform.CurrentActivity as AndroidX.Fragment.App.FragmentActivity;
            var manager = activity is null ? null : BiometricManager.From(activity);

            // Prefer biometrics; fall back to biometrics-or-device-credential (PIN/pattern/
            // password) when there is no hardware or nothing enrolled.
            var authenticators = BiometricManager.Authenticators.BiometricWeak;
            var can = manager?.CanAuthenticate(authenticators) ?? BiometricManager.BiometricErrorNoHardware;
            if (can != BiometricManager.BiometricSuccess)
            {
                authenticators = BiometricManager.Authenticators.BiometricWeak
                                 | BiometricManager.Authenticators.DeviceCredential;
                can = manager?.CanAuthenticate(authenticators) ?? BiometricManager.BiometricErrorNoHardware;
            }

            if (activity is null || can != BiometricManager.BiometricSuccess)
            {
#if DEBUG
                return await DevBypassAsync(ct);
#else
                _mechanismName = "None";
                return false;
#endif
            }

            var usesDeviceCredential = (authenticators & BiometricManager.Authenticators.DeviceCredential) != 0;
            _mechanismName = usesDeviceCredential ? "Screen lock" : "Fingerprint";
            var tcs = new TaskCompletionSource<bool>();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    var callback = new AuthCallback(tcs);
                    var prompt = new BiometricPrompt(activity, ContextCompat.GetMainExecutor(activity), callback);
                    var builder = new BiometricPrompt.PromptInfo.Builder()
                        .SetTitle("Parent settings")
                        .SetSubtitle(reason)
                        .SetAllowedAuthenticators(authenticators);
                    // A negative button is forbidden when DeviceCredential is allowed.
                    if (!usesDeviceCredential) builder.SetNegativeButtonText("Cancel");
                    prompt.Authenticate(builder.Build());
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            });

            using (ct.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }
        catch
        {
            _mechanismName = "None";
            return false;
        }
    }

    private sealed class AuthCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public AuthCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) =>
            _tcs.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) =>
            _tcs.TrySetResult(false);

        public override void OnAuthenticationFailed()
        {
            // Individual mismatch — the prompt stays up; do not complete.
        }
    }
#else
    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct) => DevBypassAsync(ct);
#endif

    private async Task<bool> DevBypassAsync(CancellationToken ct)
    {
        await Task.Delay(300, ct);
        _mechanismName = "DEV BYPASS";
        return true;
    }
}
