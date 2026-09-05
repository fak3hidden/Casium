using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using QuorumAPI;

namespace Casium.Services
{
    /// <summary>
    /// Wraps the QuorumAPI module (credit: Salad, discord.gg/YwwFwjetq2).
    ///
    /// Different QuorumAPI builds expose slightly different members (StartCommunication vs
    /// AutoUpdate, AttachAPI returning void / Task / Task&lt;State&gt;, ...). The only thing we
    /// depend on at compile time is the QuorumModule type; every member is resolved by name
    /// at runtime so the project builds against any of them.
    /// </summary>
    public sealed class QuorumBridge
    {
        private readonly Dispatcher _ui;
        private readonly Action<string, string> _log;   // (category, message)
        private QuorumModule _quorum;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        public bool IsReady { get { return _quorum != null; } }

        public QuorumBridge(Dispatcher ui, Action<string, string> log)
        {
            _ui = ui;
            _log = log;
        }

        // ---------- reflection helpers ------------------------------------------------------

        private static MethodInfo Find(string name, int argCount)
        {
            var all = typeof(QuorumModule).GetMethods(Any).Where(m => m.Name == name).ToList();
            return all.FirstOrDefault(m => m.GetParameters().Length == argCount)
                ?? all.OrderBy(m => m.GetParameters().Length).FirstOrDefault(m => m.GetParameters().Length > argCount);
        }

        /// <summary>Pads the supplied args with defaults for any extra parameters of the overload.</summary>
        private static object[] FillArgs(MethodInfo m, object[] args)
        {
            var ps = m.GetParameters();
            if (ps.Length == args.Length) return args;
            var full = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i < args.Length) { full[i] = args[i]; continue; }
                var p = ps[i];
                if (p.HasDefaultValue) full[i] = p.DefaultValue;
                else if (p.ParameterType == typeof(bool)) full[i] = true;          // e.g. nocmd = true
                else if (p.ParameterType == typeof(string)) full[i] = string.Empty;
                else if (p.ParameterType.IsValueType) full[i] = Activator.CreateInstance(p.ParameterType);
                else full[i] = null;
            }
            return full;
        }

        public static string DescribeApi()
        {
            try
            {
                var names = typeof(QuorumModule).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")" +
                                 (m.ReturnType == typeof(void) ? "" : " : " + m.ReturnType.Name))
                    .Distinct().OrderBy(n => n);
                return string.Join("; ", names);
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static bool Has(string name, int argCount)
        {
            return typeof(QuorumModule).GetMethods(Any).Any(m => m.Name == name && m.GetParameters().Length == argCount);
        }

        private static void SetStatic(string name, object value)
        {
            var f = typeof(QuorumModule).GetField(name, Any);
            if (f != null && f.IsStatic) { f.SetValue(null, value); return; }
            var p = typeof(QuorumModule).GetProperty(name, Any);
            if (p != null && p.CanWrite) p.SetValue(null, value);
        }

        /// <summary>Invoke by name; awaits if the method returns a Task; returns Task&lt;T&gt;.Result if any.</summary>
        private async Task<object> CallAsync(string name, params object[] args)
        {
            var method = Find(name, args.Length);
            if (method == null)
            {
                throw new MissingMethodException("QuorumModule." + name);
            }
            object result = method.Invoke(method.IsStatic ? null : _quorum, FillArgs(method, args));
            var task = result as Task;
            if (task == null)
            {
                return result;
            }
            await task;
            var type = task.GetType();
            if (type.IsGenericType)
            {
                var prop = type.GetProperty("Result");
                return prop != null ? prop.GetValue(task) : null;
            }
            return null;
        }

        private object Call(string name, params object[] args)
        {
            var method = Find(name, args.Length);
            if (method == null)
            {
                throw new MissingMethodException("QuorumModule." + name);
            }
            return method.Invoke(method.IsStatic ? null : _quorum, FillArgs(method, args));
        }

        // ---------- lifecycle -----------------------------------------------------------------

        public bool Init(string workspacePath = null, string autoexecPath = null)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(workspacePath)) workspacePath = System.IO.Path.Combine(baseDir, "scripts");
                if (string.IsNullOrEmpty(autoexecPath)) autoexecPath = System.IO.Path.Combine(baseDir, "autoexec");

                SetStatic("_AutoUpdateLogs", true);
                SetStatic("DumbMode", false);

                _quorum = new QuorumModule();
                Log("sys", "Quorum API: " + DescribeApi());

                if (Has("StartCommunication", 0)) Call("StartCommunication");
                else if (Has("AutoUpdate", 0)) Call("AutoUpdate");

                if (Has("SetWorkspacePath", 1)) { try { Call("SetWorkspacePath", workspacePath); } catch { } }
                if (Has("SetAutoexecPath", 1)) { try { Call("SetAutoexecPath", autoexecPath); } catch { } }
                if (Has("SetAttachNotify", 2)) { try { Call("SetAttachNotify", "Casium", "Successfully attached."); } catch { } }

                return true;
            }
            catch (Exception ex)
            {
                _quorum = null;
                Log("err", "Quorum API failed to initialise: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        public void Shutdown()
        {
            try { if (_quorum != null && Has("StopCommunication", 0)) Call("StopCommunication"); } catch { }
            _quorum = null;
        }

        private void Log(string cat, string msg)
        {
            if (_ui.CheckAccess()) _log(cat, msg);
            else _ui.BeginInvoke(new Action(() => _log(cat, msg)));
        }

        // ---------- api -----------------------------------------------------------------------

        /// <summary>Attach to all Roblox clients. Returns the Quorum state name.</summary>
        public async Task<string> AttachAsync()
        {
            if (_quorum == null) return "Error";
            try
            {
                object r;
                if (Has("AttachAPI", 0) || Find("AttachAPI", 0) != null)
                {
                    r = await CallAsync("AttachAPI");
                }
                else if (Find("Attach", 1) != null)
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
                    if (procs.Length == 0) return "NoProcessFound";
                    r = null;
                    foreach (var p in procs)
                    {
                        r = await CallAsync("Attach", p.Id);
                    }
                }
                else
                {
                    Log("err", "This QuorumAPI build has no AttachAPI/Attach method. API: " + DescribeApi());
                    return "Error";
                }
                if (r != null && !(r is bool)) return r.ToString();
                if (r is bool && (bool)r) return "Attached";

                // void/Task builds: give the injector a moment, then ask
                for (int i = 0; i < 20 && !IsAttached(); i++)
                {
                    await Task.Delay(500);
                }
                return IsAttached() ? "Attached" : "NotAttached";
            }
            catch (Exception ex)
            {
                Log("err", "Attach failed: " + (ex.InnerException ?? ex).Message);
                return "Error";
            }
        }

        public bool IsAttached()
        {
            try
            {
                if (_quorum == null || !Has("IsAttached", 0)) return false;
                object r = Call("IsAttached");
                return r is bool && (bool)r;
            }
            catch { return false; }
        }

        /// <summary>Execute in all attached clients.</summary>
        public bool Execute(string script)
        {
            if (_quorum == null) return false;
            try
            {
                object r = null;
                if (Find("ExecuteScript", 1) != null)
                {
                    r = Call("ExecuteScript", script ?? string.Empty);
                }
                else if (Find("Execute", 2) != null)
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta"))
                    {
                        r = Call("Execute", p.Id, script ?? string.Empty);
                    }
                }
                else
                {
                    Log("err", "This QuorumAPI build has no ExecuteScript/Execute method.");
                    return false;
                }
                string text = r == null ? "" : r.ToString();
                if (text.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("NotAttached", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("err", "Execute result: " + text);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("err", "Execute failed: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        public void SetAutoAttach(bool on)
        {
            try { if (_quorum != null && Has("SetAutoAttach", 1)) Call("SetAutoAttach", on); } catch { }
        }

        public void KillRoblox()
        {
            try { if (Has("KillRoblox", 0)) Call("KillRoblox"); }
            catch (Exception ex) { Log("err", (ex.InnerException ?? ex).Message); }
        }
    }
}
