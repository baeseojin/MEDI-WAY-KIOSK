namespace MediWay.Kiosk.Services;

public static class PatientBarcode
{
    // Scanner supplies decoded text followed by Enter; this layer never opens URLs.
    public static bool TryParse(string? raw, out string number)
    {
        number = "";
        if (raw is null || raw.Length > 32) return false;
        var value = raw.Trim().ToUpperInvariant();
        if (value.StartsWith("MEDIWAY:", StringComparison.Ordinal)) value = value[8..];
        if (value.StartsWith('P')) value = value[1..];
        if (value.Length is < 1 or > 11 || value.Any(c => !char.IsAsciiDigit(c))) return false;
        number = "P" + value;
        return true;
    }
}
