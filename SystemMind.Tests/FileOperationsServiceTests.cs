using System.Runtime.InteropServices;
using SystemMind.Services;
using Xunit;

namespace SystemMind.Tests;

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
        var tempFolder = Path.Combine(Path.GetTempPath(), "SystemMind_TestSafeDelete");
        Assert.False(ops.IsProtected(tempFolder));
    }
}
