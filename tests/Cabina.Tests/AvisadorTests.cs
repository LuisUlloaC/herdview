using System;
using System.Linq;
using Cabina.Modelo;
using Xunit;

namespace Cabina.Tests
{
    public class AvisadorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

        private static Sesion S(string id, string estado, bool sinDatos = false) =>
            new Sesion { Id = id, EstadoTexto = estado, SinDatos = sinDatos };

        [Fact]
        public void Avisa_al_terminar_o_esperar_pero_no_de_lo_que_ya_estaba_al_abrir()
        {
            var a = new Avisador();
            Assert.Empty(a.QueAvisar(new[] { S("a", "terminado"), S("b", "trabajando") }, T0));
            Assert.Empty(a.QueAvisar(new[] { S("a", "terminado"), S("b", "trabajando") }, T0.AddSeconds(1)));
            var avisos = a.QueAvisar(new[] { S("a", "terminado"), S("b", "esperando") }, T0.AddSeconds(2));
            Assert.Equal(new[] { "b" }, avisos.Select(s => s.Id));
            Assert.Equal(new[] { "b" }, a.QueAvisar(new[] { S("a", "terminado"), S("b", "terminado") }, T0.AddSeconds(3)).Select(s => s.Id));
        }

        [Fact]
        public void No_repite_el_mismo_aviso_seguido_ni_avisa_de_trabajar()
        {
            var a = new Avisador();
            a.QueAvisar(new[] { S("a", "trabajando") }, T0);
            Assert.Single(a.QueAvisar(new[] { S("a", "terminado") }, T0.AddSeconds(1)));
            Assert.Empty(a.QueAvisar(new[] { S("a", "trabajando") }, T0.AddSeconds(2)));
            Assert.Empty(a.QueAvisar(new[] { S("a", "terminado") }, T0.AddSeconds(5)));
            a.QueAvisar(new[] { S("a", "trabajando") }, T0.AddSeconds(30));
            Assert.Single(a.QueAvisar(new[] { S("a", "terminado") }, T0.AddSeconds(40)));
        }

        [Fact]
        public void Las_sesiones_sin_datos_no_avisan()
        {
            var a = new Avisador();
            a.QueAvisar(new[] { S("p", "inicio", true) }, T0);
            Assert.Empty(a.QueAvisar(new[] { S("p", "terminado", true) }, T0.AddSeconds(1)));
        }
    }
}
