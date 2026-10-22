using System;
using System.Collections.Generic;
using System.Linq;

namespace Cabina.Modelo
{
    /// <summary>Reglas de qué se enseña y cómo: orden, estado de conjunto (bandeja) y textos de tiempo.</summary>
    public static class Panorama
    {
        /// <summary>Primero lo que te necesita, luego lo que trabaja, lo terminado sin ver y al final el resto; dentro de
        /// cada grupo, lo más reciente arriba.</summary>
        public static List<Sesion> Ordenar(IEnumerable<Sesion> sesiones, Func<Sesion, bool> vista)
        {
            return sesiones.OrderBy(s => Prioridad(s, vista)).ThenByDescending(s => s.Desde).ToList();
        }

        /// <summary>Cómo se pinta: lo terminado que ya viste se apaga (luz gris), como una sesión en reposo.</summary>
        public static EstadoSesion Visual(Sesion s, bool vista)
        {
            return s.Estado == EstadoSesion.Terminado && vista ? EstadoSesion.Inicio : s.Estado;
        }

        /// <summary>El color de la bandeja: el estado más urgente de todas (null si no hay nada que contar).</summary>
        public static EstadoSesion? Global(IEnumerable<Sesion> sesiones, Func<Sesion, bool> vista)
        {
            var lista = sesiones.ToList();
            if (lista.Any(s => s.Estado == EstadoSesion.Esperando)) return EstadoSesion.Esperando;
            if (lista.Any(s => s.Estado == EstadoSesion.Trabajando)) return EstadoSesion.Trabajando;
            if (lista.Any(s => s.Estado == EstadoSesion.Terminado && !vista(s))) return EstadoSesion.Terminado;
            return null;
        }

        /// <summary>Cronómetro corto: «45 s», «3:07», «1 h 05».</summary>
        public static string Duracion(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            return $"{(int)t.TotalHours} h {t.Minutes:00}";
        }

        /// <summary>Hace cuánto: «ahora», «hace 4 min», «hace 2 h», «hace 3 d».</summary>
        public static string Hace(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "ahora";
            if (t.TotalHours < 1) return $"hace {(int)t.TotalMinutes} min";
            if (t.TotalDays < 1) return $"hace {(int)t.TotalHours} h";
            return $"hace {(int)t.TotalDays} d";
        }

        public static string TiempoDe(Sesion s, DateTime ahoraUtc)
        {
            var t = ahoraUtc - s.DesdeUtc;
            return s.Estado == EstadoSesion.Trabajando || s.Estado == EstadoSesion.Esperando ? Duracion(t) : Hace(t);
        }

        public static string NombreEstado(EstadoSesion e)
        {
            switch (e)
            {
                case EstadoSesion.Trabajando: return "trabajando";
                case EstadoSesion.Esperando: return "te espera";
                case EstadoSesion.Terminado: return "terminó";
                default: return "abierta";
            }
        }

        private static int Prioridad(Sesion s, Func<Sesion, bool> vista)
        {
            switch (s.Estado)
            {
                case EstadoSesion.Esperando: return 0;
                case EstadoSesion.Trabajando: return 1;
                case EstadoSesion.Terminado: return vista(s) ? 3 : 2;
                default: return 4;
            }
        }
    }
}
