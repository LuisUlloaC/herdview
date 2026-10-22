using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Herdview
{
    /// <summary>Cierra una sesión de Claude Code en Windows: primero como si pulsaras Ctrl+C en su consola (Claude sale
    /// ordenadamente y corren sus hooks de cierre) y, si a los pocos segundos sigue viva, la cierra a la fuerza con todo
    /// lo que cuelga de ella.</summary>
    internal static class Cerrador
    {
        private const uint CTRL_C_EVENT = 0;
        private static readonly object Consola = new object();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint pid);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern bool GenerateConsoleCtrlEvent(uint evento, uint grupo);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(IntPtr manejador, bool añadir);

        /// <summary>Bloquea hasta que la sesión se cierra (hasta ~5 s): llamarlo fuera del hilo de la interfaz.
        /// Devuelve cómo se cerró.</summary>
        public static string Cerrar(int pid)
        {
            if (CtrlC(pid) && Esperar(pid, TimeSpan.FromSeconds(4)))
            {
                return "cerrada";
            }
            using (var p = Process.Start(new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                p?.WaitForExit(5000);
            }
            return Esperar(pid, TimeSpan.FromSeconds(1)) ? "cerrada a la fuerza" : "no se pudo cerrar";
        }

        /// <summary>Ctrl+C tres veces a la consola del proceso (Claude pide dos seguidos para salir; si estaba trabajando,
        /// el primero solo corta el turno). Falla si no tiene consola (p. ej. la de VS Code).</summary>
        private static bool CtrlC(int pid)
        {
            lock (Consola)
            {
                FreeConsole();
                if (!AttachConsole((uint)pid))
                {
                    return false;
                }
                // Herdview no tiene consola propia: que el Ctrl+C no le afecte a él mientras está enganchado a la ajena.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                        Thread.Sleep(350);
                    }
                }
                finally
                {
                    FreeConsole();
                }
                return true;
            }
        }

        private static bool Esperar(int pid, TimeSpan limite)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    return p.WaitForExit((int)limite.TotalMilliseconds);
                }
            }
            catch (ArgumentException)
            {
                return true; // ya no existe
            }
        }
    }
}
