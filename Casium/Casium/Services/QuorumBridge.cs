using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuorumAPI;

namespace Casium.Services
{
    /// <summary>
    /// Wraps the QuorumAPI module (credit: Salad, discord.gg/YwwFwjetq2).
    /// Requires QuorumAPI.dll + its "bin" folder next to Casium.exe and an x64 build.
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

        public bool Init()
        {
            try
            {
                QuorumModule._AutoUpdateLogs = true;
                _quorum = new QuorumModule();
                _quorum.StartCommunication();   // must be called before anything else
                try { QuorumModule.SetAttachNotify("Casium", "Successfully attached."); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                _quorum = null;
                Log("err", "Quorum API failed to initialise: " + ex.Message);
                return false;
            }
        }

        public void Shutdown()
        {
            try { if (_quorum != null) _quorum.StopCommunication(); } catch { }
            _quorum = null;
        }

        private void Log(string cat, string msg)
        {
            if (_ui.CheckAccess()) _log(cat, msg);
            else _ui.BeginInvoke(new Action(() => _log(cat, msg)));
        }

        /// <summary>Attach to all Roblox clients. Returns the Quorum state name.</summary>
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

        /// <summary>Execute in all attached clients.</summary>
        public bool Execute(string script)
        {
            if (_quorum == null) return false;
            try
            {
                _quorum.ExecuteScript(script ?? string.Empty);
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
