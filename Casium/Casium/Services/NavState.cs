using System.Windows;

namespace Casium.Services
{
    /// <summary>Attached flag used by control templates to render an "active" state.</summary>
    public static class NavState
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(NavState),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static bool GetIsActive(DependencyObject obj) { return (bool)obj.GetValue(IsActiveProperty); }
        public static void SetIsActive(DependencyObject obj, bool value) { obj.SetValue(IsActiveProperty, value); }
    }
}
