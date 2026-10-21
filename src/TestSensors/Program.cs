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
            var computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            Console.WriteLine("Computer.Open() OK");

            foreach (var hw in computer.Hardware)
            {
                hw.Update();
                Console.WriteLine($"HW: {hw.Name} ({hw.HardwareType})");
                foreach (var s in hw.Sensors)
                {
                    Console.WriteLine($"  Sensor: {s.Name} | {s.SensorType} | Value: '{s.Value}'");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCECAO: {ex}");
        }
    }
}
