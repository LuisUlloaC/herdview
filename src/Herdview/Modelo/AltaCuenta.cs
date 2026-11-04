using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Herdview.Modelo
{
    /// <summary>Crea cuentas nuevas de Claude Code («.claude-&lt;nombre&gt;»), si se quiere con los ajustes de otra.
    /// El inicio de sesión lo hace el usuario en la propia sesión: Herdview nunca toca credenciales.</summary>
    public static class AltaCuenta
    {
        /// <summary>Lo que se copia de la cuenta de origen: configuración, nunca credenciales (.credentials.json),
        /// identidad (.claude.json) ni historial.</summary>
        public static readonly string[] Ficheros = { "settings.json", "CLAUDE.md" };
        public static readonly string[] Carpetas = { "agents", "skills", "commands", "output-styles", "plugins" };

        private static readonly Regex NombreValido = new Regex("^[a-z0-9][a-z0-9-]{0,29}$");
        private static readonly Regex RutaHook = new Regex(@"[A-Za-z]:(?:\\\\|/)[^""]*?herdview\.py", RegexOptions.IgnoreCase);

        /// <summary>null si el nombre vale; si no, por qué no (con la carpeta del usuario mira también si ya existe).</summary>
        public static string? ErrorNombre(string? carpetaUsuario, string nombre)
        {
            if (nombre.Length == 0)
            {
                return "Ponle un nombre a la cuenta.";
            }
            if (!NombreValido.IsMatch(nombre))
            {
                return "Solo minúsculas, números y guiones (hasta 30).";
            }
            if (nombre == "normal")
            {
                return "«normal» es la cuenta de .claude; elige otro nombre.";
            }
            if (carpetaUsuario != null && Directory.Exists(Path.Combine(carpetaUsuario, ".claude-" + nombre)))
            {
                return "Ya existe .claude-" + nombre + ".";
            }
            return null;
        }

        /// <summary>Crea «.claude-&lt;nombre&gt;» con los ajustes de <paramref name="copiarDe"/> (o vacía) y siempre con
        /// settings.json, para que salga en la lista de cuentas.</summary>
        public static Cuenta Crear(string carpetaUsuario, string nombre, Cuenta? copiarDe)
        {
            string? error = ErrorNombre(carpetaUsuario, nombre);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
            string destino = Path.Combine(carpetaUsuario, ".claude-" + nombre);
            Directory.CreateDirectory(destino);
            if (copiarDe != null && Directory.Exists(copiarDe.Carpeta))
            {
                foreach (string f in Ficheros.Where(f => File.Exists(Path.Combine(copiarDe.Carpeta, f))))
                {
                    File.Copy(Path.Combine(copiarDe.Carpeta, f), Path.Combine(destino, f));
                }
                foreach (string c in Carpetas.Where(c => Directory.Exists(Path.Combine(copiarDe.Carpeta, c))))
                {
                    CopiarCarpeta(Path.Combine(copiarDe.Carpeta, c), Path.Combine(destino, c));
                }
                ReubicarPlugins(Path.Combine(destino, "plugins"), copiarDe.Carpeta, destino);
            }
            string ajustes = Path.Combine(destino, "settings.json");
            if (!File.Exists(ajustes))
            {
                File.WriteAllText(ajustes, "{}\n");
            }
            return new Cuenta(nombre, destino, sinSesion: true);
        }

        /// <summary>Si los hooks de Herdview ya están en el settings.json de esa carpeta de cuenta.</summary>
        public static bool TieneHook(string carpetaCuenta)
        {
            string ajustes = Path.Combine(carpetaCuenta, "settings.json");
            return File.Exists(ajustes) && File.ReadAllText(ajustes).IndexOf("herdview.py", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>El herdview.py que ya usan otras cuentas (para que todas compartan el mismo), o null.</summary>
        public static string? HookConocido(IEnumerable<Cuenta> cuentas)
        {
            foreach (var c in cuentas)
            {
                string ajustes = Path.Combine(c.Carpeta, "settings.json");
                if (!File.Exists(ajustes))
                {
                    continue;
                }
                var m = RutaHook.Match(File.ReadAllText(ajustes));
                if (m.Success)
                {
                    string ruta = m.Value.Replace(@"\\", @"\");
                    if (File.Exists(ruta))
                    {
                        return ruta;
                    }
                }
            }
            return null;
        }

        /// <summary>installed_plugins.json y known_marketplaces.json guardan rutas absolutas a la carpeta de la cuenta:
        /// las copiadas apuntarían a la de origen.</summary>
        private static void ReubicarPlugins(string plugins, string origen, string destino)
        {
            if (!Directory.Exists(plugins))
            {
                return;
            }
            var cambios = new[]
            {
                (Path.Combine(origen, "plugins").Replace(@"\", @"\\"), Path.Combine(destino, "plugins").Replace(@"\", @"\\")),
                (Path.Combine(origen, "plugins").Replace('\\', '/'), Path.Combine(destino, "plugins").Replace('\\', '/')),
            };
            foreach (string json in Directory.GetFiles(plugins, "*.json"))
            {
                string texto = File.ReadAllText(json);
                string nuevo = cambios.Aggregate(texto, (t, c) =>
                    Regex.Replace(t, Regex.Escape(c.Item1), c.Item2.Replace("$", "$$"), RegexOptions.IgnoreCase));
                if (nuevo != texto)
                {
                    File.WriteAllText(json, nuevo);
                }
            }
        }

        private static void CopiarCarpeta(string origen, string destino)
        {
            Directory.CreateDirectory(destino);
            foreach (string f in Directory.GetFiles(origen))
            {
                File.Copy(f, Path.Combine(destino, Path.GetFileName(f)));
            }
            foreach (string d in Directory.GetDirectories(origen))
            {
                CopiarCarpeta(d, Path.Combine(destino, Path.GetFileName(d)));
            }
        }
    }
}
