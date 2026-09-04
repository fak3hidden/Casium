using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;

namespace Casium.Services
{
    /// <summary>
    /// Thin wrapper around the QuorumMonaco WinForms control so MainMenu can talk to it
    /// without caring whether the DLL is actually referenced. When QUORUM is not defined
    /// every call is a no-op and IsAvailable is false.
    /// </summary>
    public sealed class QuorumEditorHost
    {
        public WindowsFormsHost Host { get; private set; }
        public bool IsAvailable { get; private set; }

#if QUORUM
        private QuorumMonaco.QuorumMonaco _editor;
#endif

        public bool TryCreate()
        {
#if QUORUM
            try
            {
                _editor = new QuorumMonaco.QuorumMonaco { Dock = System.Windows.Forms.DockStyle.Fill };
                Host = new WindowsFormsHost { Child = _editor, Visibility = Visibility.Collapsed };
                QuorumMonaco.CoreFunctions.SetMonaco(_editor);
                IsAvailable = true;
            }
            catch
            {
                IsAvailable = false;
                Host = null;
            }
#endif
            return IsAvailable;
        }

        public void SetText(string code)
        {
#if QUORUM
            if (IsAvailable) QuorumMonaco.CoreFunctions.SetText(code ?? string.Empty);
#endif
        }

        public async Task<string> GetTextAsync()
        {
#if QUORUM
            if (IsAvailable) return await QuorumMonaco.CoreFunctions.GetText();
#endif
            await Task.Yield();
            return null;
        }

        public void Refresh()
        {
#if QUORUM
            if (IsAvailable) QuorumMonaco.CoreFunctions.Refresh();
#endif
        }
    }
}
