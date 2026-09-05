using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;

namespace Casium.Services
{
    /// <summary>
    /// Hosts Salad's QuorumMonaco editor (credit: Salad, discord.gg/YwwFwjetq2).
    ///
    /// The DLL is loaded by reflection instead of a compile-time reference because it ships
    /// with the WebView2 assemblies merged in, which would otherwise produce ambiguous-type
    /// errors against Casium's own WebView2 NuGet package. It also means Casium still builds
    /// and runs (with the fallback editor) when the DLL is not next to the exe.
    ///
    /// Expected API (per the QuorumMonaco readme):
    ///   QuorumMonaco.CoreFunctions.SetMonaco(control)
    ///   QuorumMonaco.CoreFunctions.SetText(string)
    ///   Task&lt;string&gt; QuorumMonaco.CoreFunctions.GetText()
    ///   QuorumMonaco.CoreFunctions.Refresh()
    /// </summary>
    public sealed class QuorumEditorHost
    {
        /// <summary>WPF element to place in the editor area (WindowsFormsHost or the control itself).</summary>
        public UIElement Element { get; private set; }
        public bool IsAvailable { get; private set; }
        public string LastError { get; private set; }

        private object _control;
        private MethodInfo _setText, _getText, _refresh;

        // Direct WebView2 access (preferred): lets us await script execution and do our own
        // escaping, which avoids the fire-and-forget races in CoreFunctions.SetText/GetText.
        private object _webView;
        private MethodInfo _execScript;
        private readonly System.Threading.SemaphoreSlim _gate = new System.Threading.SemaphoreSlim(1, 1);

        public bool TryCreate()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(dir, "QuorumMonaco.dll");
                if (!File.Exists(path))
                {
                    path = Path.Combine(dir, "Libs", "QuorumMonaco.dll");
                }
                if (!File.Exists(path))
                {
                    LastError = "QuorumMonaco.dll not found";
                    return false;
                }

                // Downloaded DLLs carry a "from the internet" zone mark which makes LoadFrom
                // refuse them. Strip the mark and use UnsafeLoadFrom (trusted local component).
                Unblock(path);
                Assembly asm = Assembly.UnsafeLoadFrom(path);

                Type core = asm.GetTypes().FirstOrDefault(t => t.Name == "CoreFunctions");
                if (core == null)
                {
                    throw new Exception("CoreFunctions type not found in QuorumMonaco.dll");
                }

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
                MethodInfo setMonaco = core.GetMethods(F).FirstOrDefault(m => m.Name == "SetMonaco" && m.GetParameters().Length == 1);
                _setText = core.GetMethods(F).FirstOrDefault(m => m.Name == "SetText" && m.GetParameters().Length == 1);
                _getText = core.GetMethods(F).FirstOrDefault(m => m.Name == "GetText" && m.GetParameters().Length == 0);
                _refresh = core.GetMethods(F).FirstOrDefault(m => m.Name == "Refresh" && m.GetParameters().Length == 0);
                if (setMonaco == null || _setText == null || _getText == null)
                {
                    throw new Exception("CoreFunctions.SetMonaco/SetText/GetText not found");
                }

                object coreInstance = null;
                if (!setMonaco.IsStatic)
                {
                    coreInstance = Activator.CreateInstance(core);
                }

                // The control type is whatever SetMonaco accepts. Prefer a concrete subclass
                // defined in the DLL itself (e.g. a "Monaco" user control); fall back to the
                // parameter type when it is concrete.
                Type paramType = setMonaco.GetParameters()[0].ParameterType;
                Type ctrlType = asm.GetTypes().FirstOrDefault(t =>
                                    !t.IsAbstract && paramType.IsAssignableFrom(t) &&
                                    t.Namespace != null && t.Namespace.StartsWith("QuorumMonaco"))
                                ?? (paramType.IsAbstract ? null : paramType);
                if (ctrlType == null)
                {
                    throw new Exception("No usable editor control type for " + paramType.FullName);
                }

                _control = Activator.CreateInstance(ctrlType);

                var winForms = _control as System.Windows.Forms.Control;
                var wpf = _control as UIElement;
                if (winForms != null)
                {
                    winForms.Dock = System.Windows.Forms.DockStyle.Fill;
                    Element = new WindowsFormsHost { Child = winForms };
                }
                else if (wpf != null)
                {
                    Element = wpf;
                }
                else
                {
                    throw new Exception(ctrlType.FullName + " is neither a WinForms nor a WPF control");
                }

                setMonaco.Invoke(coreInstance, new[] { _control });
                _coreInstance = coreInstance;
                FindWebView(_control);
                IsAvailable = true;
                return true;
            }
            catch (ReflectionTypeLoadException ex)
            {
                LastError = string.Join("; ", ex.LoaderExceptions.Select(e => e.Message).Distinct());
            }
            catch (Exception ex)
            {
                LastError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            }
            IsAvailable = false;
            Element = null;
            return false;
        }

        private object _coreInstance;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string name);

        private static void Unblock(string path)
        {
            try { DeleteFile(path + ":Zone.Identifier"); } catch { }
        }

        private void FindWebView(object root)
        {
            try
            {
                var wf = root as System.Windows.Forms.Control;
                if (wf != null)
                {
                    foreach (System.Windows.Forms.Control c in wf.Controls)
                    {
                        if (c.GetType().Name == "WebView2")
                        {
                            _webView = c;
                            break;
                        }
                        FindWebView(c);
                        if (_webView != null) break;
                    }
                }
                if (_webView == null)
                {
                    // maybe stored in a field (e.g. private WebView2 webView21)
                    foreach (FieldInfo f in root.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.FieldType.Name == "WebView2")
                        {
                            _webView = f.GetValue(root);
                            if (_webView != null) break;
                        }
                    }
                }
                if (_webView != null)
                {
                    _execScript = _webView.GetType().GetMethod("ExecuteScriptAsync", new[] { typeof(string) });
                    if (_execScript == null) _webView = null;
                }
            }
            catch
            {
                _webView = null;
            }
        }

        private async Task<string> RunScriptAsync(string js)
        {
            var task = (Task)_execScript.Invoke(_webView, new object[] { js });
            await task;
            PropertyInfo prop = task.GetType().GetProperty("Result");
            return prop != null ? prop.GetValue(task) as string : null;
        }

        private static string EncodeJs(string value)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\u2028': sb.Append("\\u2028"); break;
                    case '\u2029': sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u" + ((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string DecodeJs(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return ser.Deserialize<string>(raw);
            }
            catch
            {
                return null;
            }
        }

        public void SetText(string code)
        {
            var _ = SetTextAsync(code);
        }

        public async Task SetTextAsync(string code)
        {
            if (!IsAvailable) return;
            await _gate.WaitAsync();
            try
            {
                if (_webView != null)
                {
                    // wait until the page's editor exists, then set the value
                    for (int i = 0; i < 40; i++)
                    {
                        string ok = await RunScriptAsync("(typeof SetText === 'function') ? 'ok' : ''");
                        if (DecodeJs(ok) == "ok") break;
                        await Task.Delay(250);
                    }
                    await RunScriptAsync("SetText(" + EncodeJs(code ?? string.Empty) + ")");
                }
                else
                {
                    _setText.Invoke(_coreInstance, new object[] { code ?? string.Empty });
                }
            }
            catch { }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetTextAsync()
        {
            if (!IsAvailable) return null;
            await _gate.WaitAsync();
            try
            {
                if (_webView != null)
                {
                    string raw = await RunScriptAsync("(typeof GetText === 'function') ? GetText() : null");
                    return DecodeJs(raw);
                }
                object result = _getText.Invoke(_coreInstance, null);
                var task = result as Task;
                if (task != null)
                {
                    await task;
                    PropertyInfo prop = task.GetType().GetProperty("Result");
                    return prop != null ? prop.GetValue(task) as string : null;
                }
                return result as string;
            }
            catch
            {
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Refresh()
        {
            if (!IsAvailable || _refresh == null) return;
            try { _refresh.Invoke(_coreInstance, null); } catch { }
        }
    }
}
