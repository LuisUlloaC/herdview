using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Herdview.Modelo
{
    /// <summary>El comando «claude-&lt;nombre&gt;» de una cuenta en los perfiles de PowerShell: la misma función que se escribe
    /// a mano (pone CLAUDE_CONFIG_DIR y lanza claude). Solo añade al final del perfil; nunca toca lo que ya hay.</summary>
    public static class PerfilPowerShell
    {
        /// <summary>Los perfiles donde va el comando: el de Windows PowerShell siempre y el de PowerShell 7 si está su
        /// carpeta. <paramref name="documentos"/> es «Mis documentos» (puede estar en OneDrive).</summary>
        public static List<string> Rutas(string documentos)
        {
            var rutas = new List<string> { Path.Combine(documentos, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1") };
            if (Directory.Exists(Path.Combine(documentos, "PowerShell")))
            {
                rutas.Add(Path.Combine(documentos, "PowerShell", "Microsoft.PowerShell_profile.ps1"));
            }
            return rutas;
        }

        public static string Comando(string nombre) => "claude-" + nombre;

        /// <summary>Las líneas que se añaden: un comentario y la función.</summary>
        public static string Bloque(string nombre) =>
            "# Claude Code - cuenta " + nombre + " (config aislada)\r\n" +
            "function " + Comando(nombre) + " { $env:CLAUDE_CONFIG_DIR = \"$HOME\\.claude-" + nombre + "\"; claude @args }\r\n";

        /// <summary>Si el texto del perfil ya define ese comando (como función o alias).</summary>
        public static bool Define(string texto, string nombre) =>
            Regex.IsMatch(texto, @"(?im)^\s*(function\s+|Set-Alias\s+(-Name\s+)?)" + Regex.Escape(Comando(nombre)) + @"(?![\w-])");

        /// <summary>Añade el comando al perfil (lo crea si no existe). false si ya lo tenía.</summary>
        public static bool Anadir(string perfil, string nombre)
        {
            if (!File.Exists(perfil))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(perfil)!);
                // Con BOM: Windows PowerShell lee como ANSI los .ps1 en UTF-8 sin él.
                File.WriteAllText(perfil, Bloque(nombre), new UTF8Encoding(true));
                return true;
            }
            byte[] bytes = File.ReadAllBytes(perfil);
            Encoding codificacion = Codificacion(bytes);
            string texto = Texto(bytes, codificacion);
            if (Define(texto, nombre))
            {
                return false;
            }
            string antes = texto.Length == 0 ? "" : texto.EndsWith("\n") ? "\r\n" : "\r\n\r\n";
            // Se escriben solo los bytes nuevos al final, en la codificación del fichero (sin repetir el BOM).
            byte[] nuevo = codificacion.GetBytes(antes + Bloque(nombre));
            using (var fichero = new FileStream(perfil, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fichero.Write(nuevo, 0, nuevo.Length);
            }
            return true;
        }

        /// <summary>La del BOM; sin BOM, UTF-8 (lo añadido es ASCII, así que vale también si el fichero es ANSI).</summary>
        private static Encoding Codificacion(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                return new UnicodeEncoding(false, false);
            }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                return new UnicodeEncoding(true, false);
            }
            return new UTF8Encoding(false);
        }

        private static string Texto(byte[] b, Encoding codificacion)
        {
            int bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3
                : codificacion is UnicodeEncoding ? 2 : 0;
            return codificacion.GetString(b, bom, b.Length - bom);
        }
    }
}
