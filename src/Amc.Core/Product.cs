namespace Amc.Core;

/// <summary>Product facts shown to users. Drop <see cref="Stage"/> (set it to null) when the beta ends.</summary>
public static class Product
{
    public const string Name = "Overhive";
    public const string? Stage = "beta";

    /// <summary>"0.1.0" from an assembly version, plus " (beta)" while in beta.</summary>
    public static string Describe(Version? v) =>
        (v is null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}") + (Stage is null ? "" : $" ({Stage})");
}
