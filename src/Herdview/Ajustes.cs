using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Microsoft.Win32;

namespace Herdview
{
    /// <summary>Preferencias del usuario: dónde va la tira y si se ve. Arrancar con Windows vive en el registro (Run).</summary>
    [DataContract]
    public sealed class Ajustes
    {
        private const string ClaveRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string NombreRun = "Herdview";

        [DataMember(Name = "tira_visible")] public bool TiraVisible { get; set; } = true;
        [DataMember(Name = "tira_x")] public double? TiraX { get; set; }
        [DataMember(Name = "tira_y")] public double? TiraY { get; set; }

        private static string Ruta =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herdview", "ajustes.json");

        public static Ajustes Cargar()
        {
            try
            {
                using (var f = File.OpenRead(Ruta))
                {
                    return new DataContractJsonSerializer(typeof(Ajustes)).ReadObject(f) as Ajustes ?? new Ajustes();
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is SerializationException)
            {
                return new Ajustes { TiraVisible = true };
            }
        }

        public void Guardar()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
                using (var f = File.Create(Ruta))
                {
                    new DataContractJsonSerializer(typeof(Ajustes)).WriteObject(f, this);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        public static bool ArrancaConWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ClaveRun))
                {
                    return k?.GetValue(NombreRun) != null;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(ClaveRun))
                {
                    if (value)
                    {
                        k.SetValue(NombreRun, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    }
                    else
                    {
                        k.DeleteValue(NombreRun, false);
                    }
                }
            }
        }
    }
}
