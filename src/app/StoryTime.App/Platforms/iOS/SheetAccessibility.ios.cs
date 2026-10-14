using UIKit;

namespace StoryTime.App.Controls;

public static partial class SheetAccessibility
{
    /// <summary>iOS: <c>accessibilityViewIsModal</c> on the host makes VoiceOver ignore the
    /// host's siblings (the page content and the other overlay hosts); a screen-changed
    /// notification moves focus into the sheet, and a layout-changed one hands it back.</summary>
    public static partial void SetModal(VisualElement host, bool modal, VisualElement? focus)
    {
        if (host.Handler?.PlatformView is not UIView hostView) return;
        if (hostView.AccessibilityViewIsModal == modal) return;
        hostView.AccessibilityViewIsModal = modal;
        if (modal)
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged,
                focus?.Handler?.PlatformView as UIView);
        else
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.LayoutChanged, null);
    }
}
