Put QuorumMonaco.dll (and its dependencies, e.g. index.html / any WebView2 files that ship with it) in this folder.

When the DLL is present the project defines the QUORUM symbol and Casium uses QuorumMonaco as the editor.
When it is missing, Casium falls back to its own WebView2 Monaco page / built-in editor and still builds.

Credits: Salad (discord.gg/YwwFwjetq2)
