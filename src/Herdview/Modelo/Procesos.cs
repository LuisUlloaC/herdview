using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Herdview.Modelo
{
    public sealed class VentanaInfo
    {
        public VentanaInfo(IntPtr hwnd, string titulo)
        {
            Hwnd = hwnd;
            Titulo = titulo;
        }

        public IntPtr Hwnd { get; }
        public string Titulo { get; }
    }

    public sealed class ProcesoVivo
    {
        public ProcesoVivo(int pid, int padre, string exe)
        {
            Pid = pid;
            Padre = padre;
            Exe = exe;
        }

        public int Pid { get; }
        public int Padre { get; }
        public string Exe { get; }
    }

    /// <summary>Lo que hace falta saber de los procesos del PC (se sustituye en los tests).</summary>
    public interface IConsultaProcesos
    {
        /// <summary>Foto de todos los procesos: pid → (padre, exe con «.exe»).</summary>
        IReadOnlyDictionary<int, ProcesoVivo> Tabla();

        /// <summary>Ventanas de primer nivel visibles y con título del proceso.</summary>
        IReadOnlyList<VentanaInfo> Ventanas(int pid);

        /// <summary>Carpeta de trabajo del proceso o null si no se puede leer.</summary>
        string? Carpeta(int pid);

        /// <summary>Cuándo arrancó (UTC) o null.</summary>
        DateTime? Inicio(int pid);
    }

    /// <summary>Con la cadena de procesos que anota el hook: ¿sigue viva la sesión? ¿cuál es su ventana? Y las sesiones
    /// de Claude que aún no han escrito nada (abiertas antes de instalar el hook o sin actividad desde entonces).</summary>
    public static class Procesos
    {
        private static readonly string[] Claudes = { "claude.exe", "node.exe" };

        /// <summary>Padres de un Claude Code de verdad: un shell, una terminal o VS Code. Así no se cuela Claude Desktop
        /// (también claude.exe) ni los procesos hijos de otro Claude.</summary>
        private static readonly string[] PadresDeCli =
        {
            "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "sh.exe", "windowsterminal.exe", "openconsole.exe",
            "conhost.exe", "code.exe", "code - insiders.exe", "cursor.exe", "wezterm-gui.exe", "alacritty.exe",
        };

        /// <summary>Índice en la cadena del proceso de Claude (el primero que se llame claude.exe o node.exe), o -1.</summary>
        public static int IndiceClaude(IReadOnlyList<ProcesoAncestro> cadena)
        {
            for (int i = 0; i < cadena.Count; i++)
            {
                if (Array.IndexOf(Claudes, cadena[i].Exe.ToLowerInvariant()) >= 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool Mismo(IReadOnlyDictionary<int, ProcesoVivo> tabla, ProcesoAncestro p) =>
            tabla.TryGetValue(p.Pid, out var vivo) && string.Equals(vivo.Exe, p.Exe, StringComparison.OrdinalIgnoreCase);

        /// <summary>La sesión está viva si su proceso de Claude existe y sigue siendo el mismo programa (los PID se
        /// reutilizan). Sin cadena no se puede saber: se da por viva.</summary>
        public static bool Viva(Sesion s, IReadOnlyDictionary<int, ProcesoVivo> tabla)
        {
            int i = IndiceClaude(s.Procesos);
            return i < 0 || Mismo(tabla, s.Procesos[i]);
        }

        /// <summary>La primera ventana subiendo desde Claude: la de Windows Terminal, la consola o la de VS Code. Si el
        /// proceso tiene varias (VS Code con varias carpetas), la que lleva el nombre del proyecto en el título.</summary>
        public static IntPtr VentanaDe(Sesion s, IConsultaProcesos consulta)
        {
            var tabla = consulta.Tabla();
            int desde = Math.Max(0, IndiceClaude(s.Procesos));
            for (int i = desde; i < s.Procesos.Count; i++)
            {
                if (!Mismo(tabla, s.Procesos[i]))
                {
                    continue;
                }
                var ventanas = consulta.Ventanas(s.Procesos[i].Pid);
                if (ventanas.Count == 0)
                {
                    continue;
                }
                var suya = ventanas.FirstOrDefault(v => s.Proyecto.Length > 1
                    && v.Titulo.IndexOf(s.Proyecto, StringComparison.OrdinalIgnoreCase) >= 0);
                return (suya ?? ventanas[0]).Hwnd;
            }
            return IntPtr.Zero;
        }

        /// <summary>Para las sesiones sin ventana propia en Windows (las de un contenedor): la ventana de Windows Terminal o
        /// de VS Code cuyo título lleve alguno de los textos (el contenedor, el proyecto); si no, la de Windows Terminal.</summary>
        public static IntPtr VentanaPorTitulo(IConsultaProcesos consulta, IEnumerable<string> textos)
        {
            var buscados = textos.Where(t => t.Length > 1).ToList();
            IntPtr terminal = IntPtr.Zero;
            foreach (var p in consulta.Tabla().Values)
            {
                bool esTerminal = p.Exe.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase);
                if (!esTerminal && !p.Exe.Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var v in consulta.Ventanas(p.Pid))
                {
                    if (buscados.Any(t => v.Titulo.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return v.Hwnd;
                    }
                    if (esTerminal && terminal == IntPtr.Zero)
                    {
                        terminal = v.Hwnd;
                    }
                }
            }
            return terminal;
        }

        /// <summary>El nombre que se enseña de una carpeta: la última parte, «~» para la carpeta de usuario.</summary>
        public static string NombreProyecto(string cwd, string carpetaUsuario)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return "Claude";
            }
            string limpia = cwd.TrimEnd('\\', '/');
            if (string.Equals(limpia, carpetaUsuario.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return "~";
            }
            string nombre = Path.GetFileName(limpia);
            return nombre.Length > 0 ? nombre : limpia;
        }

        /// <summary>Los Claude Code en marcha que no tienen sesión anotada, como sesiones «sin datos» (en gris).</summary>
        public static List<Sesion> SinDatos(IEnumerable<Sesion> conocidas, IConsultaProcesos consulta, string carpetaUsuario)
        {
            var tabla = consulta.Tabla();
            var anotados = new HashSet<int>(conocidas.Select(s => IndiceClaude(s.Procesos) is int i && i >= 0 ? s.Procesos[i].Pid : 0));
            var resultado = new List<Sesion>();
            foreach (var p in tabla.Values.Where(p => string.Equals(p.Exe, "claude.exe", StringComparison.OrdinalIgnoreCase)))
            {
                if (anotados.Contains(p.Pid) || !tabla.TryGetValue(p.Padre, out var padre)
                    || Array.IndexOf(PadresDeCli, padre.Exe.ToLowerInvariant()) < 0)
                {
                    continue;
                }
                var cadena = new List<ProcesoAncestro>();
                for (var q = p; q != null && cadena.Count < 14; q = tabla.TryGetValue(q.Padre, out var sig) && sig.Pid != q.Pid ? sig : null)
                {
                    cadena.Add(new ProcesoAncestro { Pid = q.Pid, Exe = q.Exe });
                }
                string cwd = consulta.Carpeta(p.Pid) ?? "";
                DateTime inicio = consulta.Inicio(p.Pid) ?? DateTime.UtcNow;
                resultado.Add(new Sesion
                {
                    Id = "proceso-" + p.Pid,
                    Cwd = cwd,
                    Proyecto = NombreProyecto(cwd, carpetaUsuario),
                    Origen = cadena.Any(c => c.Exe.Equals("code.exe", StringComparison.OrdinalIgnoreCase)) ? "VS Code"
                        : cadena.Any(c => c.Exe.Equals("windowsterminal.exe", StringComparison.OrdinalIgnoreCase)) ? "Terminal" : "Consola",
                    EstadoTexto = "inicio",
                    Desde = new DateTimeOffset(inicio).ToUnixTimeMilliseconds() / 1000.0,
                    Procesos = cadena,
                    SinDatos = true,
                });
            }
            return resultado;
        }
    }
}
