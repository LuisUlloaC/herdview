using System;
using System.IO;
using System.Text;
using Cabina.Modelo;
using Xunit;

namespace Cabina.Tests
{
    public class BuzonTests : IDisposable
    {
        private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "cabina-buzon-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_carpeta))
            {
                Directory.Delete(_carpeta, true);
            }
        }

        [Fact]
        public void El_nombre_coincide_con_el_del_hook()
        {
            Assert.Equal(Path.Combine(_carpeta, "a94dab6a-ad15_x.txt"), Buzon.RutaDe(_carpeta, "a94dab6a-ad15_x"));
            Assert.Equal(Path.Combine(_carpeta, "ab.txt"), Buzon.RutaDe(_carpeta, "a/../b"));
        }

        [Fact]
        public void Los_mensajes_sin_recoger_se_juntan_y_no_quedan_temporales()
        {
            Buzon.Enviar(_carpeta, "s1", "  hola ");
            Buzon.Enviar(_carpeta, "s1", "sigue con los lados");
            Assert.Equal("hola\n\nsigue con los lados", File.ReadAllText(Buzon.RutaDe(_carpeta, "s1"), Encoding.UTF8));
            Assert.Single(Directory.GetFiles(_carpeta));
        }
    }
}
