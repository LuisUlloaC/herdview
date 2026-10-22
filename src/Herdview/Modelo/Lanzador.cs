using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Herdview.Modelo
{
    /// <summary>Una cuenta de Claude Code: su carpeta de configuración («.claude» es la normal, sin CLAUDE_CONFIG_DIR).</summary>
    public sealed class Cuenta
    {
        public Cuenta(string nombre, string carpeta)
        {
            Nombre = nombre;
            Carpeta = carpeta;
        }

        public string Nombre { get; }
        public string Carpeta { get; }
        public bool EsNormal => Nombre == "normal";
    }

    /// <summary>Abre sesiones nuevas de Claude Code en una pestaña de Windows Terminal (o en una consola si no hay).</summary>
    public static class Lanzador
    {
        /// <summary>Las cuentas del usuario: «.claude» (normal) y cada «.claude-&lt;nombre&gt;» que tenga configuración.</summary>
        public static List<Cuenta> Cuentas(string carpetaUsuario)
        {
            var cuentas = new List<Cuenta>();
            if (!Directory.Exists(carpetaUsuario))
            {
                return cuentas;
            }
            foreach (string d in Directory.GetDirectories(carpetaUsuario, ".claude-*").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(d, "settings.json")) || Directory.Exists(Path.Combine(d, "projects")))
                {
                    cuentas.Add(new Cuenta(Path.GetFileName(d).Substring(".claude-".Length), d));
                }
            }
            string normal = Path.Combine(carpetaUsuario, ".claude");
            if (Directory.Exists(normal))
            {
                cuentas.Add(new Cuenta("normal", normal));
            }
            return cuentas;
        }

        /// <summary>El script de PowerShell que arranca Claude con esa cuenta y, si lo hay, el primer pedido.</summary>
        public static string Script(Cuenta cuenta, string pedido)
        {
            var s = new StringBuilder();
            s.AppendLine(cuenta.EsNormal
                ? "Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue"
                : "$env:CLAUDE_CONFIG_DIR = " + Literal(cuenta.Carpeta));
            s.Append(string.IsNullOrWhiteSpace(pedido) ? "claude" : "claude " + Literal(pedido.Trim()));
            return s.ToString();
        }

        /// <summary>Argumentos de wt.exe: pestaña nueva en la ventana más reciente, en la carpeta y con el proyecto como
        /// título. El script va en -EncodedCommand: así ni los «;» (separador de wt) ni las comillas del pedido rompen nada.</summary>
        public static string ArgumentosTerminal(string carpeta, Cuenta cuenta, string pedido)
        {
            string limpia = carpeta.TrimEnd('\\', '/');
            string titulo = Path.GetFileName(limpia);
            return "-w 0 new-tab --title " + Comillas(titulo.Length > 0 ? titulo : "Claude") + " -d " + Comillas(limpia) +
                " powershell.exe " + ArgumentosPowerShell(cuenta, pedido);
        }

        public static string ArgumentosPowerShell(Cuenta cuenta, string pedido) =>
            "-NoLogo -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(cuenta, pedido)));

        /// <summary>Cadena literal de PowerShell (comillas simples; las de dentro se doblan).</summary>
        public static string Literal(string texto) => "'" + texto.Replace("'", "''") + "'";

        private static string Comillas(string texto) => "\"" + texto.Replace("\"", "") + "\"";
    }

    /// <summary>Las carpetas donde has abierto Claude, la más reciente primero (para elegir sin buscar).</summary>
    public sealed class Recientes
    {
        private const int Maximo = 12;
        private readonly List<string> _carpetas = new List<string>();

        public IReadOnlyList<string> Carpetas => _carpetas;

        public Recientes(IEnumerable<string>? iniciales = null)
        {
            foreach (string c in iniciales ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(c) && !_carpetas.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    _carpetas.Add(c);
                }
            }
        }

        /// <summary>La pone la primera. Devuelve si cambió algo (para guardar solo entonces).</summary>
        public bool Usar(string carpeta)
        {
            string limpia = carpeta.TrimEnd('\\', '/');
            if (limpia.Length == 0 || (_carpetas.Count > 0 && string.Equals(_carpetas[0], limpia, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            _carpetas.RemoveAll(c => string.Equals(c, limpia, StringComparison.OrdinalIgnoreCase));
            _carpetas.Insert(0, limpia);
            if (_carpetas.Count > Maximo)
            {
                _carpetas.RemoveRange(Maximo, _carpetas.Count - Maximo);
            }
            return true;
        }

        /// <summary>Añade al final si no está (las de sesiones vistas: no desplazan a las que elegiste tú).</summary>
        public bool Conocer(string carpeta)
        {
            string limpia = carpeta.TrimEnd('\\', '/');
            if (limpia.Length == 0 || _carpetas.Contains(limpia, StringComparer.OrdinalIgnoreCase) || _carpetas.Count >= Maximo)
            {
                return false;
            }
            _carpetas.Add(limpia);
            return true;
        }
    }
}
