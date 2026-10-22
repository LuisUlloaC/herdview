using System;
using System.Collections.Generic;

namespace Cabina.Modelo
{
    /// <summary>Decide qué sesiones merecen notificación: las que acaban de pasar a «terminó» o a «te espera». La primera
    /// carga solo toma nota (no avisa de lo que ya estaba así al abrir Cabina) y no repite el mismo aviso de una sesión
    /// en poco tiempo (un hook de Stop que la hace seguir la termina dos veces seguidas).</summary>
    public sealed class Avisador
    {
        private static readonly TimeSpan Silencio = TimeSpan.FromSeconds(20);

        private readonly Dictionary<string, EstadoSesion> _previos = new Dictionary<string, EstadoSesion>();
        private readonly Dictionary<string, (EstadoSesion Estado, DateTime Cuando)> _ultimos =
            new Dictionary<string, (EstadoSesion, DateTime)>();
        private bool _primera = true;

        public List<Sesion> QueAvisar(IEnumerable<Sesion> sesiones, DateTime ahoraUtc)
        {
            var avisar = new List<Sesion>();
            var vistas = new HashSet<string>();
            foreach (var s in sesiones)
            {
                if (s.SinDatos)
                {
                    continue;
                }
                vistas.Add(s.Id);
                bool cambia = _previos.TryGetValue(s.Id, out var previo) && previo != s.Estado;
                _previos[s.Id] = s.Estado;
                if (_primera || !cambia || (s.Estado != EstadoSesion.Terminado && s.Estado != EstadoSesion.Esperando))
                {
                    continue;
                }
                if (_ultimos.TryGetValue(s.Id, out var ultimo) && ultimo.Estado == s.Estado && ahoraUtc - ultimo.Cuando < Silencio)
                {
                    continue;
                }
                _ultimos[s.Id] = (s.Estado, ahoraUtc);
                avisar.Add(s);
            }
            foreach (var id in new List<string>(_previos.Keys))
            {
                if (!vistas.Contains(id))
                {
                    _previos.Remove(id);
                    _ultimos.Remove(id);
                }
            }
            _primera = false;
            return avisar;
        }
    }
}
