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

                Assembly asm = Assembly.LoadFrom(path);

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

        public void SetText(string code)
        {
            if (!IsAvailable) return;
            try { _setText.Invoke(_coreInstance, new object[] { code ?? string.Empty }); } catch { }
        }

        public async Task<string> GetTextAsync()
        {
            if (!IsAvailable) return null;
            try
            {
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
        }

        public void Refresh()
        {
            if (!IsAvailable || _refresh == null) return;
            try { _refresh.Invoke(_coreInstance, null); } catch { }
        }
    }
}
