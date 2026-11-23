<#
.SYNOPSIS  Sends Ctrl+C to a console process (graceful stop of the agent when it runs as a console host).
.NOTES     Runs in its own process because attaching to another console detaches the caller's console.
#>
param([Parameter(Mandatory)][uint32]$ProcessId)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CtrlCSender {
  [DllImport("kernel32.dll")] static extern bool FreeConsole();
  [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
  [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint evt, uint grp);
  public static bool Send(uint pid) {
    FreeConsole();
    if (!AttachConsole(pid)) return false;
    SetConsoleCtrlHandler(IntPtr.Zero, true);
    bool ok = GenerateConsoleCtrlEvent(0, 0);
    System.Threading.Thread.Sleep(500);
    FreeConsole();
    return ok;
  }
}
'@
if ([CtrlCSender]::Send($ProcessId)) { exit 0 } else { exit 1 }
