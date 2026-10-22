using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Herdview.Modelo
{
    /// <summary>La carpeta donde el hook deja un JSON por sesión. Lee todas, borra las de sesiones cuyo Claude ya no
    /// existe (se cerró la ventana sin SessionEnd) y avisa cuando algo cambia.</summary>
    public sealed class AlmacenSesiones : IDisposable
    {
        /// <summary>Margen antes de dar por muerta una sesión: el hook puede estar escribiendo su primera línea.</summary>
        private static readonly TimeSpan Gracia = TimeSpan.FromSeconds(20);

        private readonly string _carpeta;
        private readonly IConsultaProcesos _procesos;
        private FileSystemWatcher? _vigia;

        public AlmacenSesiones(string carpeta, IConsultaProcesos procesos)
        {
            _carpeta = carpeta;
            _procesos = procesos;
        }

        public static string CarpetaPorDefecto =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herdview", "sesiones");

        /// <summary>Se lanza (en un hilo del sistema) cuando cambia algún fichero de la carpeta.</summary>
        public event EventHandler? Cambio;

        public void Vigilar()
        {
            Directory.CreateDirectory(_carpeta);
            _vigia = new FileSystemWatcher(_carpeta, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler avisar = (_, __) => Cambio?.Invoke(this, EventArgs.Empty);
            _vigia.Changed += avisar;
            _vigia.Created += avisar;
            _vigia.Deleted += avisar;
            _vigia.Renamed += (_, __) => Cambio?.Invoke(this, EventArgs.Empty);
            _vigia.EnableRaisingEvents = true;
        }

        public List<Sesion> Cargar(DateTime ahoraUtc)
        {
            var sesiones = new List<Sesion>();
            if (!Directory.Exists(_carpeta))
            {
                return sesiones;
            }
            var tabla = _procesos.Tabla();
            foreach (string ruta in Directory.GetFiles(_carpeta, "*.json"))
            {
                Sesion? s = LeerFichero(ruta);
                if (s == null)
                {
                    continue;
                }
                var tocado = DateTimeOffset.FromUnixTimeMilliseconds((long)(s.Ts * 1000)).UtcDateTime;
                if (ahoraUtc - tocado > Gracia && !Procesos.Viva(s, tabla))
                {
                    Borrar(ruta);
                    continue;
                }
                sesiones.Add(s);
            }
            return sesiones;
        }

        public void Dispose()
        {
            _vigia?.Dispose();
        }

        private static Sesion? LeerFichero(string ruta)
        {
            // El hook escribe con os.replace: o se lee entero o falla al abrir; un fallo se arregla en la siguiente vuelta.
            try
            {
                return Sesion.Leer(File.ReadAllText(ruta, Encoding.UTF8));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Borrar(string ruta)
        {
            try
            {
                File.Delete(ruta);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
