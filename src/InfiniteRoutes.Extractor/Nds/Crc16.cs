namespace InfiniteRoutes.Extractor.Nds;

/// <summary>CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF), as used by the NDS header checksum.</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}
