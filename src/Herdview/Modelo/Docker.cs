using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Herdview.Modelo
{
    public sealed class Contenedor
    {
        public Contenedor(string id, string nombre, string imagen)
        {
            Id = id;
            Nombre = nombre;
            Imagen = imagen;
        }

        public string Id { get; }
        public string Nombre { get; }
        public string Imagen { get; }
    }

    /// <summary>Lo que Herdview le pide a Docker (se sustituye en los tests).</summary>
    public interface IDocker
    {
        /// <summary>Contenedores en marcha (vacío si Docker no está instalado o no está arrancado).</summary>
        IReadOnlyList<Contenedor> EnMarcha();

        /// <summary>`docker exec [-i] &lt;contenedor&gt; sh -c &lt;script&gt; sh &lt;args…&gt;`: (código, salida).</summary>
        (int Codigo, string Salida) Sh(string contenedor, string script, string? entrada = null, params string[] args);
    }

    /// <summary>Herdview y los contenedores: el hook vive en ~/.herdview/herdview.py dentro de cada uno y se le habla
    /// por `docker exec` (sin montar carpetas ni abrir puertos).</summary>
    public static class EnDocker
    {
        public const string Hook = "\"$HOME/.herdview/herdview.py\"";

        /// <summary>Las sesiones de Claude de todos los contenedores preparados, con el nombre del contenedor puesto.</summary>
        public static List<Sesion> Sesiones(IDocker docker)
        {
            var sesiones = new List<Sesion>();
            foreach (var c in docker.EnMarcha())
            {
                var (codigo, salida) = docker.Sh(c.Id, "f=" + Hook + "; [ -f \"$f\" ] && python3 \"$f\" listar");
                if (codigo != 0)
                {
                    continue;
                }
                foreach (string linea in salida.Split('\n'))
                {
                    var s = linea.Trim().Length > 0 ? Sesion.Leer(linea.Trim()) : null;
                    if (s != null)
                    {
                        s.Contenedor = c.Nombre;
                        s.ContenedorId = c.Id;
                        s.Origen = "Docker · " + c.Nombre;
                        sesiones.Add(s);
                    }
                }
            }
            return sesiones;
        }

        /// <summary>Deja el mensaje en el buzón de la sesión, dentro de su contenedor.</summary>
        public static bool Enviar(IDocker docker, Sesion s, string texto) =>
            docker.Sh(s.ContenedorId, "python3 " + Hook + " enviar \"$1\"", texto, s.Id).Codigo == 0;

        /// <summary>Cierra el Claude de la sesión dentro del contenedor: TERM (sale ordenadamente) y, si en 3 s sigue,
        /// KILL. Con el PID desconocido no hace nada.</summary>
        public static bool Cerrar(IDocker docker, Sesion s)
        {
            if (s.PidClaude <= 0)
            {
                return false;
            }
            const string script = "kill -TERM \"$1\" 2>/dev/null || exit 0; for i in 1 2 3 4 5 6; do " +
                "kill -0 \"$1\" 2>/dev/null || exit 0; sleep 0.5; done; kill -KILL \"$1\"";
            return docker.Sh(s.ContenedorId, script, null, s.PidClaude.ToString(System.Globalization.CultureInfo.InvariantCulture)).Codigo == 0;
        }

        public enum Preparacion { Lista, SinPython, Fallo }

        /// <summary>Copia el hook en el contenedor (siempre: así se actualiza) y lo registra en su Claude Code.</summary>
        public static (Preparacion Resultado, string Detalle) Preparar(IDocker docker, string contenedor, string hook)
        {
            if (docker.Sh(contenedor, "command -v python3").Codigo != 0)
            {
                return (Preparacion.SinPython, "El contenedor no tiene python3. Instálalo (apt-get install -y python3 o apk add python3) y vuelve a probar.");
            }
            var copia = docker.Sh(contenedor, "mkdir -p \"$HOME/.herdview\" && cat > " + Hook, hook);
            if (copia.Codigo != 0)
            {
                return (Preparacion.Fallo, "No pude copiar el hook: " + copia.Salida.Trim());
            }
            var instala = docker.Sh(contenedor, "python3 " + Hook + " instalar");
            return instala.Codigo == 0 ? (Preparacion.Lista, instala.Salida.Trim()) : (Preparacion.Fallo, "No pude registrar el hook: " + instala.Salida.Trim());
        }

        /// <summary>Script de PowerShell que abre Claude dentro del contenedor. El pedido viaja en base64 por una variable
        /// de entorno y lo decodifica el shell del contenedor: así no lo rompe ninguna capa de comillas (wt, PowerShell,
        /// docker, sh). `--%` hace que PowerShell pase el resto tal cual a docker.exe.</summary>
        public static string Script(string contenedor, string carpeta, string pedido)
        {
            var linea = new StringBuilder("docker --% exec -it");
            string arranque = "exec claude";
            if (!string.IsNullOrWhiteSpace(pedido))
            {
                linea.Append(" -e HERDVIEW_PEDIDO_B64=").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pedido.Trim())));
                arranque = "exec claude \\\"$(echo $HERDVIEW_PEDIDO_B64 | base64 -d)\\\"";
            }
            if (!string.IsNullOrWhiteSpace(carpeta))
            {
                linea.Append(" -w \"").Append(carpeta.Trim().Replace("\"", "")).Append('"');
            }
            linea.Append(' ').Append(contenedor).Append(" sh -lc \"").Append(arranque).Append('"');
            return linea.ToString();
        }

        public static string ArgumentosTerminal(string contenedor, string carpeta, string pedido) =>
            "-w 0 new-tab --title \"" + contenedor + "\" powershell.exe -NoLogo -NoExit -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(contenedor, carpeta, pedido)));
    }

    /// <summary>Docker de verdad: docker.exe con tiempo límite (si Docker Desktop está parado, falla rápido).</summary>
    public sealed class DockerCli : IDocker
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(8);

        public IReadOnlyList<Contenedor> EnMarcha()
        {
            var (codigo, salida) = Correr(new[] { "ps", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}" }, null);
            if (codigo != 0)
            {
                return Array.Empty<Contenedor>();
            }
            return salida.Split('\n').Select(l => l.Trim().Split('\t')).Where(p => p.Length == 3)
                .Select(p => new Contenedor(p[0], p[1], p[2])).ToList();
        }

        public (int Codigo, string Salida) Sh(string contenedor, string script, string? entrada = null, params string[] args)
        {
            var todos = new List<string> { "exec" };
            if (entrada != null)
            {
                todos.Add("-i");
            }
            todos.AddRange(new[] { contenedor, "sh", "-c", script, "sh" });
            todos.AddRange(args);
            return Correr(todos, entrada);
        }

        private static (int, string) Correr(IEnumerable<string> args, string? entrada)
        {
            var info = new ProcessStartInfo("docker", string.Join(" ", args.Select(Argumento)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = entrada != null,
                StandardOutputEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = Process.Start(info)!)
                {
                    if (entrada != null)
                    {
                        var bytes = new UTF8Encoding(false).GetBytes(entrada);
                        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        p.StandardInput.Close();
                    }
                    var salida = p.StandardOutput.ReadToEndAsync();
                    var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit((int)Limite.TotalMilliseconds))
                    {
                        try { p.Kill(); } catch (InvalidOperationException) { }
                        return (-1, "");
                    }
                    return (p.ExitCode, salida.Result + error.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (-1, "");
            }
        }

        /// <summary>Un argumento con las reglas de la línea de órdenes de Windows (las que usa docker.exe).</summary>
        public static string Argumento(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return a;
            }
            var s = new StringBuilder("\"");
            int barras = 0;
            foreach (char c in a)
            {
                if (c == '\\')
                {
                    barras++;
                    continue;
                }
                s.Append('\\', c == '"' ? barras * 2 + 1 : barras);
                barras = 0;
                s.Append(c);
            }
            s.Append('\\', barras * 2).Append('"');
            return s.ToString();
        }
    }
}
