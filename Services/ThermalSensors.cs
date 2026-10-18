using DiagnoX.Models;

namespace DiagnoX.Services;

/// <summary>
/// Classifies raw temperature sensors and picks the representative reading per component.
/// Kept free of I/O so the selection rules can be unit-tested.
/// </summary>
public static class ThermalSensors
{
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Storage = "Storage";
    public const string System = "System";

    private static readonly HashSet<string> CpuChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "k10temp", "zenpower", "coretemp", "cpu_thermal", "soc_thermal", "via_cputemp"
    };

    private static readonly HashSet<string> GpuChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "amdgpu", "radeon", "nouveau", "i915", "xe"
    };

    private static readonly HashSet<string> StorageChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvme", "drivetemp"
    };

    // Motherboard Super I/O chips report unconnected inputs as garbage (e.g. 113 °C or 3892313 °C).
    private static readonly string[] SuperIoPrefixes = ["nct", "it8", "w83", "f71", "f81", "sch5"];

    public static bool IsPlausible(double celsius) => celsius is > 5 and < 125;

    /// <summary>Returns the kind of a hwmon sensor, or null when the sensor should be ignored.</summary>
    public static string? Classify(string chip, string label)
    {
        if (CpuChips.Contains(chip)) return Cpu;
        if (GpuChips.Contains(chip)) return Gpu;
        if (StorageChips.Contains(chip)) return Storage;

        var cpuLabelled = label.Contains("CPU", StringComparison.OrdinalIgnoreCase);
        if (SuperIoPrefixes.Any(prefix => chip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return cpuLabelled ? Cpu : null;

        return cpuLabelled ? Cpu : System;
    }

    /// <summary>Best CPU temperature: die/package sensors win over control, per-core and board readings.</summary>
    public static double? CpuTemperature(IReadOnlyList<TemperatureReading> readings) =>
        readings.Where(reading => reading.Kind == Cpu)
            .GroupBy(CpuTier)
            .OrderBy(tier => tier.Key)
            .Select(tier => (double?)tier.Max(reading => reading.Celsius))
            .FirstOrDefault();

    /// <summary>Representative temperature for a component kind, preferring its headline sensor when present.</summary>
    public static double? ForKind(IReadOnlyList<TemperatureReading> readings, string kind)
    {
        if (kind == Cpu) return CpuTemperature(readings);

        var ofKind = readings.Where(reading => reading.Kind == kind).ToList();
        if (ofKind.Count == 0) return null;

        // amdgpu "edge" is what vendor tools show; NVMe "Composite" is the drive's own summary value.
        var headline = kind switch { Gpu => "edge", Storage => "Composite", _ => null };
        var preferred = ofKind.Where(reading => reading.Label.Equals(headline, StringComparison.OrdinalIgnoreCase)).ToList();
        return (preferred.Count > 0 ? preferred : ofKind).Max(reading => reading.Celsius);
    }

    private static int CpuTier(TemperatureReading reading)
    {
        if (!CpuChips.Contains(reading.Chip)) return 3; // laptop EC, Super I/O or thermal-zone inputs
        if (reading.Label.Equals("Tdie", StringComparison.OrdinalIgnoreCase) ||
            reading.Label.StartsWith("Package id", StringComparison.OrdinalIgnoreCase)) return 0;
        if (reading.Label.Equals("Tctl", StringComparison.OrdinalIgnoreCase)) return 1; // may carry an offset on older Ryzen
        return 2; // per-core, per-CCD or unlabelled
    }
}
