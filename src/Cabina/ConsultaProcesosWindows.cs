using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cabina.Modelo;

namespace Cabina
{
    /// <summary>Los procesos y ventanas reales del PC por Win32: foto de procesos (Toolhelp), ventanas por proceso
    /// (EnumWindows) y la carpeta de trabajo leída del bloque de proceso (PEB) de un proceso de 64 bits.</summary>
    public sealed class ConsultaProcesosWindows : IConsultaProcesos
    {
        private const uint TH32CS_SNAPPROCESS = 0x2;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint GW_OWNER = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint acceso, bool heredar, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr direccion, byte[] buffer, IntPtr tam, out IntPtr leidos);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr h, int clase, ref PROCESS_BASIC_INFORMATION info, int tam, out int devuelto);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder texto, int max);

        public IReadOnlyDictionary<int, ProcesoVivo> Tabla()
        {
            var tabla = new Dictionary<int, ProcesoVivo>();
            IntPtr foto = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (foto == IntPtr.Zero || foto == new IntPtr(-1))
            {
                return tabla;
            }
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                for (bool ok = Process32FirstW(foto, ref e); ok; ok = Process32NextW(foto, ref e))
                {
                    tabla[(int)e.th32ProcessID] = new ProcesoVivo((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile);
                }
            }
            finally
            {
                CloseHandle(foto);
            }
            return tabla;
        }

        public IReadOnlyList<VentanaInfo> Ventanas(int pid)
        {
            var ventanas = new List<VentanaInfo>();
            var texto = new StringBuilder(512);
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out int suyo);
                if (suyo == pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                {
                    texto.Clear();
                    if (GetWindowTextW(hwnd, texto, texto.Capacity) > 0)
                    {
                        ventanas.Add(new VentanaInfo(hwnd, texto.ToString()));
                    }
                }
                return true;
            }, IntPtr.Zero);
            return ventanas;
        }

        public string? Carpeta(int pid)
        {
            // Offsets de 64 bits: PEB+0x20 = ProcessParameters; +0x38 = CurrentDirectory.DosPath (UNICODE_STRING).
            if (!Environment.Is64BitProcess)
            {
                return null;
            }
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var info = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf(info), out _) != 0)
                {
                    return null;
                }
                byte[]? parametros = Leer(h, info.PebBaseAddress + 0x20, 8);
                if (parametros == null)
                {
                    return null;
                }
                byte[]? cadena = Leer(h, new IntPtr(BitConverter.ToInt64(parametros, 0)) + 0x38, 16);
                if (cadena == null)
                {
                    return null;
                }
                int largo = BitConverter.ToUInt16(cadena, 0);
                byte[]? texto = largo > 0 && largo < 4096 ? Leer(h, new IntPtr(BitConverter.ToInt64(cadena, 8)), largo) : null;
                return texto == null ? null : Encoding.Unicode.GetString(texto).TrimEnd('\\');
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public DateTime? Inicio(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    return p.StartTime.ToUniversalTime();
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        private static byte[]? Leer(IntPtr h, IntPtr direccion, int tam)
        {
            var buffer = new byte[tam];
            return ReadProcessMemory(h, direccion, buffer, new IntPtr(tam), out IntPtr leidos) && leidos.ToInt64() == tam ? buffer : null;
        }
    }
}
