using System;
using System.ComponentModel;
using System.Windows.Input;
using Herdview.Modelo;

namespace Herdview
{
    /// <summary>Una sesión tal como se pinta en la tira y en el panel. Se actualiza en su sitio para no parpadear.</summary>
    public sealed class SesionVM : INotifyPropertyChanged
    {
        private Sesion _sesion;
        private string _tiempo = "";
        private bool _vista;
        private string _nota = "";
        private readonly Func<SesionVM, string, bool> _enviar;

        public SesionVM(Sesion sesion, Action<SesionVM> enfocar, Func<SesionVM, string, bool> enviar)
        {
            _sesion = sesion;
            _enviar = enviar;
            Enfocar = new Orden(() => enfocar(this));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Sesion Sesion => _sesion;
        public string Id => _sesion.Id;
        public string Proyecto => string.IsNullOrEmpty(_sesion.Proyecto) ? "Claude" : _sesion.Proyecto;
        public string Origen => _sesion.Origen;
        /// <summary>El estado tal como se pinta (lo terminado ya visto, apagado).</summary>
        public EstadoSesion Estado => Panorama.Visual(_sesion, _vista);
        public string NombreEstado => Panorama.NombreEstado(_sesion.Estado);
        public string Tiempo => _tiempo;
        public ICommand Enfocar { get; }

        /// <summary>La línea de qué hace: lo que pide si te espera, la herramienta si trabaja.</summary>
        public string Linea
        {
            get
            {
                if (_sesion.SinDatos) return "Sin datos aún: escríbele una vez en su ventana";
                if (_sesion.Estado == EstadoSesion.Esperando && _sesion.Aviso.Length > 0) return _sesion.Aviso;
                if (_sesion.Estado == EstadoSesion.Trabajando && _sesion.Actividad.Length > 0) return _sesion.Actividad;
                if (_sesion.Estado == EstadoSesion.Terminado) return "Listo para seguir";
                if (_sesion.Estado == EstadoSesion.Inicio) return "Sin pedidos todavía";
                return "Pensando…";
            }
        }

        public string Pedido => _sesion.Pedido;
        public bool TienePedido => _sesion.Pedido.Length > 0;
        public string Detalle => Linea + (TienePedido ? "\n» " + _sesion.Pedido : "");

        /// <summary>Solo las sesiones con hook tienen buzón (las «sin datos» aún no).</summary>
        public bool PuedeEscribir => !_sesion.SinDatos;

        /// <summary>Qué pasó con el último mensaje: entregado ya o en cola hasta que termine.</summary>
        public string Nota => _nota;

        /// <summary>Manda el texto a la sesión. Devuelve si salió (para vaciar la caja).</summary>
        public bool Enviar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto) || !PuedeEscribir || !_enviar(this, texto))
            {
                return false;
            }
            _nota = _sesion.Estado == EstadoSesion.Terminado || _sesion.Estado == EstadoSesion.Inicio
                ? "Enviado ✓" : "En cola: le llega cuando termine lo que está haciendo";
            Avisar(nameof(Nota));
            return true;
        }

        public void Actualizar(Sesion sesion, bool vista, DateTime ahoraUtc)
        {
            if (sesion.Desde != _sesion.Desde)
            {
                _nota = "";
            }
            _sesion = sesion;
            _vista = vista;
            _tiempo = Panorama.TiempoDe(sesion, ahoraUtc);
            Avisar("");
        }

        public void Tic(DateTime ahoraUtc)
        {
            string t = Panorama.TiempoDe(_sesion, ahoraUtc);
            if (t != _tiempo)
            {
                _tiempo = t;
                Avisar(nameof(Tiempo));
            }
        }

        private void Avisar(string propiedad) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propiedad));

        private sealed class Orden : ICommand
        {
            private readonly Action _accion;
            public Orden(Action accion) => _accion = accion;
#pragma warning disable CS0067 // Nunca cambia: siempre se puede.
            public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _accion();
        }
    }
}
