using Seamstress.Core;
using Xunit;

namespace Seamstress.Tests;

public class AppLogTests
{
    [Fact]
    public void StartsANewDatedFileForEachRunAndWritesLines()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"seamstress-log-{Guid.NewGuid():N}");
        try
        {
            Assert.True(AppLog.Start(folder, "first run"));
            string first = Assert.IsType<string>(AppLog.FilePath);
            Assert.Equal(folder, AppLog.Folder);
            Assert.True(AppLog.IsActive);
            Assert.Matches(@"^seamstress-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.txt$", Path.GetFileName(first));
            AppLog.Write("something happened");
            string text = File.ReadAllText(first);
            Assert.Contains("first run", text);
            Assert.Contains("something happened", text);
            Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", text);

            Assert.True(AppLog.Start(folder));
            string second = Assert.IsType<string>(AppLog.FilePath);
            Assert.NotEqual(first, second);
            Assert.Equal(2, Directory.GetFiles(folder, "*.txt").Length);

            AppLog.Stop();
            Assert.False(AppLog.IsActive);
            Assert.Null(AppLog.FilePath);
            Assert.True(File.Exists(second));
            Assert.Equal(2, Directory.GetFiles(folder, "*.txt").Length);
        }
        finally
        {
            AppLog.Stop();
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void LogsLiveInTheDedicatedSeamstressFolder()
    {
        string root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(root, "Seamstress", "logs"), AppSettings.LogsFolder);
    }

    [Fact]
    public void WriteDoesNothingWhenLoggingIsOff()
    {
        AppLog.Stop();
        AppLog.Write("not written");
        Assert.False(AppLog.IsActive);
        Assert.Null(AppLog.FilePath);
    }

}
