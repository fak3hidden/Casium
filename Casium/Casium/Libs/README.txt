Third-party components (credit: Salad, discord.gg/YwwFwjetq2)

Copy the contents of the QuorumAPI download here:
  Libs\QuorumAPI.dll     - referenced by the project (Private=True, copied next to Casium.exe)
  Libs\Bin\...           - copied to <output>\Bin
  Libs\Workspace\...     - copied to <output>\Workspace
  (AutoExec / Scripts folders are not needed: Casium uses <output>\autoexec and <output>\scripts)

  Libs\QuorumMonaco.dll  - editor, loaded at runtime by reflection
  Libs\Monaco\...        - editor assets, copied to <output>\Monaco

Build with platform x64. The app manifest requests administrator.
