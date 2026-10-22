using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Herdview.Modelo
{
    /// <summary>Tokens gastados por una sesión (con sus subagentes) y el tamaño de su contexto ahora mismo.</summary>
    public sealed class Consumo
    {
        /// <summary>Entrada sin caché (input_tokens).</summary>
        public long Entrada { get; set; }
        /// <summary>Entrada que se escribió en la caché (cache_creation_input_tokens).</summary>
        public long CacheEscrita { get; set; }
        /// <summary>Entrada leída de la caché (cache_read_input_tokens): lo que se reenvía en cada turno.</summary>
        public long CacheLeida { get; set; }
        public long Salida { get; set; }
        /// <summary>Contexto de la última respuesta de la sesión principal (entrada + caché escrita + caché leída).</summary>
        public long Contexto { get; set; }
        public string Modelo { get; set; } = "";
        public int Respuestas { get; set; }

        /// <summary>Entrada «nueva»: lo que no venía de la caché.</summary>
        public long EntradaNueva => Entrada + CacheEscrita;

        public void Sumar(Consumo otro)
        {
            Entrada += otro.Entrada;
            CacheEscrita += otro.CacheEscrita;
            CacheLeida += otro.CacheLeida;
            Salida += otro.Salida;
            Respuestas += otro.Respuestas;
        }
    }

    /// <summary>Lee una transcripción de Claude Code (JSONL) de forma incremental: cada vez solo lo que se ha añadido
    /// desde la anterior. Cada respuesta aparece en varias líneas (una por bloque) con el mismo id y el mismo uso: se
    /// cuenta una vez (la última versión, por si el uso creció).</summary>
    public sealed class LectorTranscript
    {
        private static readonly Regex Id = new Regex("\"id\"\\s*:\\s*\"(msg_[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ModeloRe = new Regex("\"model\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex[] Campos =
        {
            new Regex("\"input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled),
            new Regex("\"cache_creation_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled),
            new Regex("\"cache_read_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled),
            new Regex("\"output_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled),
        };

        private readonly string _ruta;
        private readonly Dictionary<string, long[]> _porRespuesta = new Dictionary<string, long[]>();
        private long _posicion;
        private string _resto = "";
        private long[] _ultima = new long[4];
        private string _modelo = "";

        public LectorTranscript(string ruta)
        {
            _ruta = ruta;
        }

        public Consumo Leer()
        {
            LeerLoNuevo();
            var c = new Consumo { Modelo = _modelo, Respuestas = _porRespuesta.Count };
            foreach (var u in _porRespuesta.Values)
            {
                c.Entrada += u[0];
                c.CacheEscrita += u[1];
                c.CacheLeida += u[2];
                c.Salida += u[3];
            }
            c.Contexto = _ultima[0] + _ultima[1] + _ultima[2];
            return c;
        }

        private void LeerLoNuevo()
        {
            FileStream f;
            try
            {
                f = new FileStream(_ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            using (f)
            {
                if (f.Length < _posicion)
                {
                    // Se reescribió (o es otra): se empieza de cero.
                    _posicion = 0;
                    _resto = "";
                    _porRespuesta.Clear();
                }
                f.Seek(_posicion, SeekOrigin.Begin);
                using (var lector = new StreamReader(f, Encoding.UTF8, false, 1 << 16, leaveOpen: true))
                {
                    var buffer = new char[1 << 16];
                    var linea = new StringBuilder(_resto);
                    int n;
                    while ((n = lector.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        int inicio = 0;
                        for (int i = 0; i < n; i++)
                        {
                            if (buffer[i] == '\n')
                            {
                                linea.Append(buffer, inicio, i - inicio);
                                Procesar(linea.ToString());
                                linea.Clear();
                                inicio = i + 1;
                            }
                        }
                        linea.Append(buffer, inicio, n - inicio);
                    }
                    _resto = linea.ToString();
                }
                // Hasta donde se leyó de verdad (si el fichero creció mientras, lo demás va en la próxima vuelta).
                _posicion = f.Position;
            }
        }

        /// <summary>Una línea de la transcripción: si es una respuesta con uso, se anota.</summary>
        public void Procesar(string linea)
        {
            if (linea.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0)
            {
                return;
            }
            int usage = linea.IndexOf("\"usage\"", StringComparison.Ordinal);
            if (usage < 0)
            {
                return;
            }
            var id = Id.Match(linea);
            if (!id.Success)
            {
                return;
            }
            // Los campos de arriba del objeto usage vienen antes que los de «iterations» (que repiten nombres).
            string uso = linea.Substring(usage, Math.Min(1500, linea.Length - usage));
            var valores = new long[4];
            for (int k = 0; k < Campos.Length; k++)
            {
                var m = Campos[k].Match(uso);
                valores[k] = m.Success ? long.Parse(m.Groups[1].Value) : 0;
            }
            _porRespuesta[id.Groups[1].Value] = valores;
            bool lateral = linea.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) >= 0;
            var modelo = ModeloRe.Match(linea);
            if (!lateral && modelo.Success && modelo.Groups[1].Value != "<synthetic>")
            {
                _modelo = modelo.Groups[1].Value;
                _ultima = valores;
            }
        }
    }

    /// <summary>El consumo de cada sesión: su transcripción y las de sus subagentes (carpeta «&lt;sesión&gt;\subagents»).
    /// Guarda un lector por fichero para leer solo lo nuevo cada vez.</summary>
    public sealed class MedidorConsumo
    {
        private readonly Dictionary<string, LectorTranscript> _lectores =
            new Dictionary<string, LectorTranscript>(StringComparer.OrdinalIgnoreCase);

        public Consumo Medir(string transcript)
        {
            var total = Lector(transcript).Leer();
            string carpeta = Path.Combine(Path.GetDirectoryName(transcript) ?? "", Path.GetFileNameWithoutExtension(transcript), "subagents");
            if (Directory.Exists(carpeta))
            {
                foreach (string sub in Directory.GetFiles(carpeta, "*.jsonl"))
                {
                    total.Sumar(Lector(sub).Leer());
                }
            }
            return total;
        }

        private LectorTranscript Lector(string ruta)
        {
            if (!_lectores.TryGetValue(ruta, out var l))
            {
                l = new LectorTranscript(ruta);
                _lectores[ruta] = l;
            }
            return l;
        }
    }
}
