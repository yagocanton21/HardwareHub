using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace HardwareHub.Services
{
    public class HwInfoReader
    {
        private const string MemoryMapName = "Global\\HWiNFO_Shared_Memory_2";
        private const string MemoryMapNameLocal = "HWiNFO_Shared_Memory_2";

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct HWiNFO_MEM_HEADER
        {
            public uint dwSignature; // 'HWiS' (0x53695748)
            public uint dwVersion;
            public uint dwRevision;
            public long pollTime;
            public uint dwOffsetOfSensorSection;
            public uint dwSizeOfSensorElement;
            public uint dwNumSensorElements;
            public uint dwOffsetOfReadingSection;
            public uint dwSizeOfReadingElement;
            public uint dwNumReadingElements;
        }

        public class HwInfoReading
        {
            public string Label { get; set; } = "";
            public string Unit { get; set; } = "";
            public double Value { get; set; }
            public uint ReadingType { get; set; } // 1 = Temperature
        }

        public static bool IsHwInfoAvailable()
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(MemoryMapName, MemoryMappedFileRights.Read);
                return true;
            }
            catch
            {
                try
                {
                    using var mmfLocal = MemoryMappedFile.OpenExisting(MemoryMapNameLocal, MemoryMappedFileRights.Read);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static List<HwInfoReading> ReadAllSensors()
        {
            var readings = new List<HwInfoReading>();

            MemoryMappedFile? mmf = null;
            try
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(MemoryMapName, MemoryMappedFileRights.Read);
                }
                catch
                {
                    mmf = MemoryMappedFile.OpenExisting(MemoryMapNameLocal, MemoryMappedFileRights.Read);
                }

                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                // Lê o cabeçalho
                accessor.Read(0, out HWiNFO_MEM_HEADER header);

                // 'HWiS' em ASCII = 0x53695748
                if (header.dwSignature != 0x53695748 && header.dwSignature != 0x48576953)
                {
                    return readings;
                }

                int readingSize = (int)header.dwSizeOfReadingElement;
                int numReadings = (int)header.dwNumReadingElements;
                long readingOffset = header.dwOffsetOfReadingSection;

                for (int i = 0; i < numReadings; i++)
                {
                    long currentOffset = readingOffset + (i * readingSize);

                    // dwReadingType (offset + 4)
                    uint readingType = accessor.ReadUInt32(currentOffset + 4);

                    // szLabelUser (offset + 12, tamanho 128 bytes)
                    byte[] labelBytes = new byte[128];
                    accessor.ReadArray(currentOffset + 12, labelBytes, 0, 128);
                    string label = Encoding.UTF8.GetString(labelBytes).TrimEnd('\0').Trim();

                    // szUnit (offset + 140, tamanho 16 bytes)
                    byte[] unitBytes = new byte[16];
                    accessor.ReadArray(currentOffset + 140, unitBytes, 0, 16);
                    string unit = Encoding.UTF8.GetString(unitBytes).TrimEnd('\0').Trim();

                    // Value (offset + 156, double 8 bytes)
                    double val = accessor.ReadDouble(currentOffset + 156);

                    readings.Add(new HwInfoReading
                    {
                        Label = label,
                        Unit = unit,
                        Value = val,
                        ReadingType = readingType
                    });
                }
            }
            catch
            {
                // HWiNFO não está ativo ou memória fechada
            }
            finally
            {
                mmf?.Dispose();
            }

            return readings;
        }
    }
}
