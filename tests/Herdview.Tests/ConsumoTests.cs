using System;
using System.IO;
using Herdview.Modelo;
using Xunit;

namespace Herdview.Tests
{
    public class ConsumoTests : IDisposable
    {
        private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "herdview-consumo-" + Guid.NewGuid().ToString("N"));

        public ConsumoTests() => Directory.CreateDirectory(_carpeta);

        public void Dispose() => Directory.Delete(_carpeta, true);

        private static string Respuesta(string id, int entrada, int escrita, int leida, int salida, string modelo = "claude-opus-5-5",
            bool lateral = false) =>
            "{\"type\":\"assistant\",\"isSidechain\":" + (lateral ? "true" : "false") + ",\"message\":{\"model\":\"" + modelo +
            "\",\"id\":\"" + id + "\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hola\"}],\"usage\":{" +
            "\"input_tokens\":" + entrada + ",\"cache_creation_input_tokens\":" + escrita + ",\"cache_read_input_tokens\":" + leida +
            ",\"output_tokens\":" + salida + ",\"cache_creation\":{\"ephemeral_5m_input_tokens\":999},\"iterations\":[{\"input_tokens\":777}]}}}\n";

        [Fact]
        public void Cuenta_cada_respuesta_una_vez_y_el_contexto_es_el_de_la_ultima()
        {
            string t = Path.Combine(_carpeta, "s1.jsonl");
            File.WriteAllText(t, "{\"type\":\"user\",\"message\":{\"content\":\"hola\"}}\n" +
                Respuesta("msg_1", 10, 100, 1000, 5) + Respuesta("msg_1", 10, 100, 1000, 5) + Respuesta("msg_2", 2, 50, 1100, 7));
            var c = new MedidorConsumo().Medir(t);
            Assert.Equal((12L, 150L, 2100L, 12L, 2), (c.Entrada, c.CacheEscrita, c.CacheLeida, c.Salida, c.Respuestas));
            Assert.Equal(2 + 50 + 1100, c.Contexto);
            Assert.Equal("claude-opus-5-5", c.Modelo);
            Assert.Equal(162, c.EntradaNueva);
        }

        [Fact]
        public void Lee_solo_lo_nuevo_y_las_lineas_a_medias_esperan()
        {
            string t = Path.Combine(_carpeta, "s1.jsonl");
            var medidor = new MedidorConsumo();
            string r2 = Respuesta("msg_2", 1, 1, 1, 1);
            File.WriteAllText(t, Respuesta("msg_1", 1, 1, 1, 1) + r2.Substring(0, 40));
            Assert.Equal(1, medidor.Medir(t).Respuestas);
            File.AppendAllText(t, r2.Substring(40));
            Assert.Equal(2, medidor.Medir(t).Respuestas);
        }

        [Fact]
        public void Formatos_de_tokens_y_modelo()
        {
            Assert.Equal("845", Panorama.Tokens(845));
            Assert.Equal("12,3 k", Panorama.Tokens(12_345));
            Assert.Equal("512 k", Panorama.Tokens(512_400));
            Assert.Equal("4,56 M", Panorama.Tokens(4_561_000));
            Assert.Equal("123 M", Panorama.Tokens(123_456_789));
            Assert.Equal("5,63 G", Panorama.Tokens(5_626_890_782));
            Assert.Equal("Opus 5.5", Panorama.NombreModelo("claude-opus-5-5"));
            Assert.Equal("Haiku 5.5", Panorama.NombreModelo("claude-haiku-5-5-20260101"));
            Assert.Equal("", Panorama.NombreModelo(""));
        }

        [Fact]
        public void Suma_los_subagentes_pero_el_contexto_es_el_de_la_sesion()
        {
            string t = Path.Combine(_carpeta, "s1.jsonl");
            File.WriteAllText(t, Respuesta("msg_1", 1, 10, 100, 5));
            Directory.CreateDirectory(Path.Combine(_carpeta, "s1", "subagents"));
            File.WriteAllText(Path.Combine(_carpeta, "s1", "subagents", "agent-a.jsonl"),
                Respuesta("msg_9", 3, 30, 300, 9, "claude-haiku-5-5", lateral: true));
            var c = new MedidorConsumo().Medir(t);
            Assert.Equal((4L, 40L, 400L, 14L), (c.Entrada, c.CacheEscrita, c.CacheLeida, c.Salida));
            Assert.Equal(111, c.Contexto);
            Assert.Equal("claude-opus-5-5", c.Modelo);
        }
    }
}
