// ============================================================================
//  NativeMethods — CLI 模式控制台附加（WinExe 无控制台，需 AttachConsole 才可见）
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace DshController.Core
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        public const int STD_OUTPUT_HANDLE = -11;
        public const int ATTACH_PARENT_PROCESS = -1;
    }
}
