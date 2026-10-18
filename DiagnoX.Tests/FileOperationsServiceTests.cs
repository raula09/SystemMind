using System.Runtime.InteropServices;
using DiagnoX.Services;
using Xunit;

namespace DiagnoX.Tests;

public class FileOperationsServiceTests
{
    [Fact]
    public void IsProtected_ProtectsRootAndSystemDirectories()
    {
        var ops = new FileOperationsService();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.True(ops.IsProtected("/"));
            Assert.True(ops.IsProtected("/bin"));
            Assert.True(ops.IsProtected("/usr"));
            Assert.True(ops.IsProtected("/etc"));
            Assert.True(ops.IsProtected("/boot"));
            Assert.True(ops.IsProtected("/home"));

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.True(ops.IsProtected(home)); // Should not allow deleting user's entire home root!
        }
    }

    [Fact]
    public void IsProtected_AllowsSafeSubdirectories()
    {
        var ops = new FileOperationsService();
        var tempFolder = Path.Combine(Path.GetTempPath(), "DiagnoX_TestSafeDelete");
        Assert.False(ops.IsProtected(tempFolder));
    }
}
