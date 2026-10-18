using DiagnoX.Models;
using DiagnoX.Services;
using Xunit;

namespace DiagnoX.Tests;

public class ThermalAndMountTests
{
    [Theory]
    [InlineData("zenpower", "Tdie", "CPU")]
    [InlineData("k10temp", "Tctl", "CPU")]
    [InlineData("coretemp", "Package id 0", "CPU")]
    [InlineData("amdgpu", "edge", "GPU")]
    [InlineData("nvme", "Composite", "Storage")]
    [InlineData("thinkpad", "CPU", "CPU")]
    [InlineData("acpitz", "", "System")]
    [InlineData("nct6793", "CPUTIN", "CPU")]
    public void Classify_RecognisesKnownSensors(string chip, string label, string expected)
    {
        Assert.Equal(expected, ThermalSensors.Classify(chip, label));
    }

    [Theory]
    [InlineData("nct6793", "SYSTIN")]
    [InlineData("nct6793", "AUXTIN1")]
    [InlineData("it8628", "temp3")]
    public void Classify_IgnoresUnreliableSuperIoInputs(string chip, string label)
    {
        // Unconnected Super I/O inputs report values such as 113 °C, which would poison "hottest sensor".
        Assert.Null(ThermalSensors.Classify(chip, label));
    }

    [Fact]
    public void CpuTemperature_PrefersDieSensorOverBoardAndControlReadings()
    {
        var readings = new List<TemperatureReading>
        {
            new("CPU", "nct6793", "CPUTIN", 55),
            new("CPU", "k10temp", "Tctl", 70), // Zen+ adds a +10 °C offset to Tctl
            new("CPU", "k10temp", "Tdie", 60),
        };

        Assert.Equal(60, ThermalSensors.CpuTemperature(readings));
    }

    [Fact]
    public void CpuTemperature_FallsBackToHottestCoreWithoutPackageSensor()
    {
        var readings = new List<TemperatureReading>
        {
            new("CPU", "coretemp", "Core 0", 48),
            new("CPU", "coretemp", "Core 1", 53),
        };

        Assert.Equal(53, ThermalSensors.CpuTemperature(readings));
        Assert.Null(ThermalSensors.CpuTemperature([]));
    }

    [Fact]
    public void ForKind_UsesHeadlineSensorWhenPresent()
    {
        var readings = new List<TemperatureReading>
        {
            new("GPU", "amdgpu", "junction", 75),
            new("GPU", "amdgpu", "edge", 52),
            new("Storage", "nvme", "Sensor 2", 68),
            new("Storage", "nvme", "Composite", 41),
        };

        Assert.Equal(52, ThermalSensors.ForKind(readings, "GPU"));
        Assert.Equal(41, ThermalSensors.ForKind(readings, "Storage"));
        Assert.Null(ThermalSensors.ForKind(readings, "System"));
    }

    [Theory]
    [InlineData(@"NVME\x201TB", "NVME 1TB")]
    [InlineData(@"HDD\x20512GB", "HDD 512GB")]
    [InlineData(@"Caf\xc3\xa9", "Café")]
    [InlineData("plain", "plain")]
    public void DecodeUdevName_UnescapesHexBytes(string raw, string expected)
    {
        Assert.Equal(expected, SystemMonitor.DecodeUdevName(raw));
    }

    [Theory]
    [InlineData(@"/mnt/My\040Drive", "/mnt/My Drive")]
    [InlineData(@"/media/a\134b", @"/media/a\b")]
    [InlineData("/", "/")]
    public void DecodeMountField_UnescapesOctal(string raw, string expected)
    {
        Assert.Equal(expected, SystemMonitor.DecodeMountField(raw));
    }

    [Theory]
    [InlineData(0.3, 0.5, "307 / 512 MB")]
    [InlineData(700, 915, "700 / 915 GB")]
    public void DriveItem_FormatUsage_SwitchesToMegabytesForSmallVolumes(double used, double total, string expected)
    {
        Assert.Equal(expected, DriveItem.FormatUsage(used, total));
    }
}
