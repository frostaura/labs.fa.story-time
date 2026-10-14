using Foundation;

namespace StoryTime.App;

/// <summary>UIScene lifecycle adoption. iOS 27 traps at launch for apps without it
/// (UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption); Info.plist's
/// UIApplicationSceneManifest names this class.</summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
