namespace ODrive.Protocol.Tests;

internal static class TestData
{
    public const ushort ReferenceJsonCrc = 0xC1A0;
    public const uint ReferenceJsonVersionId = 0xC1A0AD28;

    /// <summary>JSON d'endpoints du firmware 0.5.6 pour v3.x (voir tools/gen_reference_json.py).</summary>
    public static byte[] ReferenceJson() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "odrive-v3.6-0.5.6.json"));
}
