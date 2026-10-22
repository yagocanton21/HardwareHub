using System;
using System.IO;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

class Program
{
    static void Main()
    {
        Console.WriteLine("Iniciando diagnstico detalhado...");
        try
        {
            var computer = new Computer 
            { 
                IsStorageEnabled = true,
                IsCpuEnabled = true,
                IsGpuEnabled = true 
            };
            computer.Open();
            Console.WriteLine("Computer.Open() OK");

            foreach (var name in Enum.GetNames(typeof(SensorType)))
            {
                Console.WriteLine($"SensorType: {name}");
            }

            var asm = typeof(Computer).Assembly;
            var diskInfoSmartAttrType = asm.GetType("DiskInfoToolkit.SmartAttribute");
            if (diskInfoSmartAttrType != null)
            {
                Console.WriteLine("=== DiskInfoToolkit.SmartAttribute Properties ===");
                foreach (var p in diskInfoSmartAttrType.GetProperties())
                {
                    Console.WriteLine($"DiskInfoSmart Prop: {p.Name} ({p.PropertyType})");
                }
            }

            var diskInfoStorageType = asm.GetType("DiskInfoToolkit.Storage");
            if (diskInfoStorageType != null)
            {
                Console.WriteLine("=== DiskInfoToolkit.Storage Properties ===");
                foreach (var p in diskInfoStorageType.GetProperties())
                {
                    Console.WriteLine($"DiskInfoStorage Prop: {p.Name} ({p.PropertyType})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCECAO: {ex}");
        }
    }
}
