using System;
using System.Runtime.InteropServices;

namespace Cabina
{
    internal static class Win32
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out PUNTO lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct PUNTO
        {
            public int X;
            public int Y;
        }

        /// <summary>La ventana en primer plano ahora mismo.</summary>
        public static IntPtr Primera() => GetForegroundWindow();

        /// <summary>Ventana que nunca se activa ni sale en la barra de tareas ni en Alt+Tab (la tira).</summary>
        public static void HacerNoActivable(IntPtr hwnd)
        {
            long estilo = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            estilo = (estilo | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~(long)WS_EX_APPWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(estilo));
        }

        /// <summary>Trae la ventana al frente. Windows solo deja hacerlo al proceso que recibió la última entrada (el clic
        /// en la tira lo es); si aun así se niega, una pulsación de Alt desbloquea el cambio de foco.</summary>
        public static bool Enfocar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
            {
                return true;
            }
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return SetForegroundWindow(hwnd);
        }
    }
}
