using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Herdview.Modelo
{
    public enum EstadoSesion
    {
        Inicio,
        Trabajando,
        Esperando,
        Terminado,
    }

    [DataContract]
    public sealed class ProcesoAncestro
    {
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "exe")] public string Exe { get; set; } = "";
    }

    /// <summary>Lo que el hook (hooks/herdview.py) escribe de cada sesión de Claude en su JSON.</summary>
    [DataContract]
    public sealed class Sesion
    {
        [DataMember(Name = "id")] public string Id { get; set; } = "";
        [DataMember(Name = "cwd")] public string Cwd { get; set; } = "";
        [DataMember(Name = "proyecto")] public string Proyecto { get; set; } = "";
        [DataMember(Name = "origen")] public string Origen { get; set; } = "";
        [DataMember(Name = "estado")] public string EstadoTexto { get; set; } = "";
        [DataMember(Name = "pedido")] public string Pedido { get; set; } = "";
        [DataMember(Name = "actividad")] public string Actividad { get; set; } = "";
        [DataMember(Name = "aviso")] public string Aviso { get; set; } = "";
        /// <summary>Segundos Unix desde que está en el estado actual.</summary>
        [DataMember(Name = "desde")] public double Desde { get; set; }
        /// <summary>Segundos Unix de la última actualización.</summary>
        [DataMember(Name = "ts")] public double Ts { get; set; }
        [DataMember(Name = "transcript")] public string Transcript { get; set; } = "";
        [DataMember(Name = "procesos")] public List<ProcesoAncestro> Procesos { get; set; } = new List<ProcesoAncestro>();

        /// <summary>Un Claude en marcha que aún no ha escrito nada (no viene del hook, sale de los procesos).</summary>
        public bool SinDatos { get; set; }

        public EstadoSesion Estado
        {
            get
            {
                switch (EstadoTexto)
                {
                    case "trabajando": return EstadoSesion.Trabajando;
                    case "esperando": return EstadoSesion.Esperando;
                    case "terminado": return EstadoSesion.Terminado;
                    default: return EstadoSesion.Inicio;
                }
            }
        }

        public DateTime DesdeUtc => DateTimeOffset.FromUnixTimeMilliseconds((long)(Desde * 1000)).UtcDateTime;

        public static Sesion? Leer(string json)
        {
            // El lector de DataContract acepta un objeto sin cerrar: un fichero a medias no es una sesión.
            if (!json.TrimEnd().EndsWith("}", StringComparison.Ordinal))
            {
                return null;
            }
            try
            {
                var serializador = new DataContractJsonSerializer(typeof(Sesion));
                using (var flujo = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var s = serializador.ReadObject(flujo) as Sesion;
                    if (s == null || string.IsNullOrEmpty(s.Id))
                    {
                        return null;
                    }
                    // DataContract no ejecuta los inicializadores: lo que no venga en el JSON queda a null.
                    s.Cwd ??= "";
                    s.Proyecto ??= "";
                    s.Origen ??= "";
                    s.EstadoTexto ??= "";
                    s.Pedido ??= "";
                    s.Actividad ??= "";
                    s.Aviso ??= "";
                    s.Transcript ??= "";
                    s.Procesos ??= new List<ProcesoAncestro>();
                    return s;
                }
            }
            catch (Exception e) when (e is SerializationException || e is System.Xml.XmlException)
            {
                return null;
            }
        }
    }
}
