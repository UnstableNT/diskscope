using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskScope.Core.Smart;

/// <summary>Name and interpretation of one ATA SMART attribute.</summary>
public sealed record AttributeDefinition(
    int Id,
    string Name,
    ValueKind Kind,
    ulong? UnitBytes = null,
    string? Source = null,
    int? RawBytes = null)
{
    public bool PercentFromCurrent => !string.Equals(Source, "raw", StringComparison.OrdinalIgnoreCase);
}

public sealed record VendorSection(string Name, IReadOnlyList<string> ModelPrefixes, IReadOnlyList<AttributeDefinition> Attributes);

public sealed record AttributeTableData(IReadOnlyList<AttributeDefinition> Generic, IReadOnlyList<VendorSection> Vendors);

/// <summary>The SMART attribute table from <c>Data/smart-attributes.json</c> (embedded resource, SPEC 4.3).</summary>
public sealed class AttributeTable
{
    private const string ResourceName = "DiskScope.Core.Data.smart-attributes.json";

    private static readonly Lazy<AttributeTable> DefaultTable = new(LoadEmbedded);

    private readonly AttributeTableData _data;

    public AttributeTable(AttributeTableData data)
    {
        _data = data;
    }

    public static AttributeTable Default => DefaultTable.Value;

    public static AttributeTable Parse(string json) =>
        new(JsonSerializer.Deserialize(json, AttributeTableJsonContext.Default.AttributeTableData)
            ?? throw new System.IO.InvalidDataException("Leere Attributtabelle."));

    /// <summary>Vendor section whose model prefix matches, or null.</summary>
    public VendorSection? FindVendor(string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : _data.Vendors.FirstOrDefault(v =>
                v.ModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Vendor-specific definition first, then the generic one, then "Unbekanntes Attribut".</summary>
    public AttributeDefinition Lookup(int id, string? model) =>
        FindVendor(model)?.Attributes.FirstOrDefault(a => a.Id == id)
        ?? _data.Generic.FirstOrDefault(a => a.Id == id)
        ?? new AttributeDefinition(id, "Unbekanntes Attribut", ValueKind.Raw);

    private static AttributeTable LoadEmbedded()
    {
        using var stream = typeof(AttributeTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Ressource {ResourceName} fehlt.");
        using var reader = new System.IO.StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AttributeTableData))]
internal sealed partial class AttributeTableJsonContext : JsonSerializerContext;
