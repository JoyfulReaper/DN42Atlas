using System.Text.Json;
using System.Text.Json.Nodes;
using DN42Atlas.Policy;
using DN42Atlas.Reporting;

namespace DN42Atlas.Publishing;

public sealed record PublicArtifacts(string Json, string Html);

public static class PublicArtifactGenerator
{
    public static PublicArtifacts Generate(byte[] rawScan, ExclusionPolicy policy)
    {
        var publicScan = JsonNode.Parse(rawScan) as JsonObject
            ?? throw new InvalidDataException("Scan JSON must be an object.");
        if (publicScan["Results"] is not JsonArray)
            throw new InvalidDataException("Scan JSON must contain a Results array.");
        new PublicScanPolicy(policy).Apply(publicScan);
        return new PublicArtifacts(
            publicScan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            AtlasReportGenerator.GenerateHtml(publicScan));
    }
}
