using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuorumAPI;

namespace Casium.Services
{
    /// <summary>
    /// Wraps the QuorumAPI module (credit: Salad, discord.gg/YwwFwjetq2) and forwards its
    /// output logger into Casium's console. All callbacks are marshalled to the UI thread.
    /// </summary>
    public sealed class QuorumBridge
    {
        private readonly Dispatcher _ui;
        private readonly Action<string, string> _log;   // (category, message)
        private QuorumModule _quorum;

        public bool IsReady { get { return _quorum != null; } }

        public QuorumBridge(Dispatcher ui, Action<string, string> log)
        {
            _ui = ui;
            _log = log;
        }

        public bool Init(string workspacePath, string autoexecPath)
        {
            try
            {
                QuorumModule._AutoUpdateLogs = true;
                QuorumModule.UseAutoUpdate = true;
                QuorumModule.UseAutoUpdateAPI = true;
                QuorumModule.DumbMode = false;   // errors go to our console instead of MessageBoxes

                _quorum = new QuorumModule();

                try { QuorumModule.SetWorkspacePath(workspacePath); } catch { }
                try { QuorumModule.SetAutoexecPath(autoexecPath); } catch { }

                // ---- output -> Casium console ----
                QuorumModule.UseOutput(true);
                QuorumModule.Logger.OnLog += OnQuorumLog;
                try
                {
                    QuorumModule.Logger.SetTheme(new COP.LogTheme
                    {
                        Info = System.Drawing.Color.White,
                        Success = System.Drawing.Color.LimeGreen,
                        Warning = System.Drawing.Color.Orange,
                        Error = System.Drawing.Color.Red,
                        System = System.Drawing.Color.Gray
                    });
                    QuorumModule.Logger.SetFormat(new COP.LogFormat
                    {
                        InfoTag = "",
                        SuccessTag = "",
                        WarningTag = "",
                        ErrorTag = "",
                        SystemTag = ""
                    });
                    QuorumModule.Logger.SetLogSource(COP.LogSource.All);
                    QuorumModule.Logger.StartRobloxLogWatcher(1000);
                }
                catch { }

                _quorum.AutoUpdate();
                return true;
            }
            catch (Exception ex)
            {
                _quorum = null;
                Log("err", "Quorum API failed to initialise: " + ex.Message);
                return false;
            }
        }

        private void OnQuorumLog(string message, System.Drawing.Color color)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string cat = "sys";
            if (color.ToArgb() == System.Drawing.Color.Red.ToArgb()) cat = "err";
            else if (color.ToArgb() == System.Drawing.Color.Orange.ToArgb()) cat = "warn";
            else if (color.ToArgb() == System.Drawing.Color.LimeGreen.ToArgb()) cat = "ok";
            else if (color.ToArgb() == System.Drawing.Color.White.ToArgb()) cat = "out";
            Log(cat, message.TrimEnd());
        }

        private void Log(string cat, string msg)
        {
            if (_ui.CheckAccess()) _log(cat, msg);
            else _ui.BeginInvoke(new Action(() => _log(cat, msg)));
        }

        public async Task<string> AttachAsync()
        {
            if (_quorum == null) return "Error";
            try
            {
                var result = await _quorum.AttachAPI();
                return result.ToString();
            }
            catch (Exception ex)
            {
                Log("err", "Attach failed: " + ex.Message);
                return "Error";
            }
        }

        public bool IsAttached()
        {
            try { return _quorum != null && _quorum.IsAttached(); }
            catch { return false; }
        }

        public bool Execute(string script)
        {
            if (_quorum == null) return false;
            try
            {
                object result = _quorum.ExecuteScript(script ?? string.Empty);
                string text = result == null ? "" : result.ToString();
                if (text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf("NotAttached", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("err", "Execute result: " + text);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("err", "Execute failed: " + ex.Message);
                return false;
            }
        }

        public void SetAutoAttach(bool on)
        {
            try { if (_quorum != null) _quorum.SetAutoAttach(on); } catch { }
        }

        public void KillRoblox()
        {
            try { QuorumModule.KillRoblox(); } catch (Exception ex) { Log("err", ex.Message); }
        }
    }
}
