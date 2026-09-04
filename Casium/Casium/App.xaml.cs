using System.Windows;
using Casium.Services;

namespace Casium
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.ApplySaved();
        }
    }
}
