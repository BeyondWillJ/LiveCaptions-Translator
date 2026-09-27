using System.Text;

namespace LiveCaptionsTranslator.Tests;

public class LauncherScriptTests
{
    [Fact]
    public void RunX64ScriptUsesPublishedArtifactPathAndReturnsProcessExitCode()
    {
        string root = FindRepositoryRoot();
        string scriptPath = Path.Combine(root, "run-win-x64.bat");
        byte[] scriptBytes = File.ReadAllBytes(scriptPath);

        Assert.All(scriptBytes, value => Assert.True(value <= 0x7F, "Launcher script must remain ASCII."));
        for (int index = 0; index < scriptBytes.Length; index++)
        {
            if (scriptBytes[index] == (byte)'\n')
                Assert.True(index > 0 && scriptBytes[index - 1] == (byte)'\r', "Launcher script must use CRLF.");
        }
        Assert.True(scriptBytes.Length >= 2);
        Assert.Equal((byte)'\r', scriptBytes[^2]);
        Assert.Equal((byte)'\n', scriptBytes[^1]);

        string script = Encoding.ASCII.GetString(scriptBytes);
        Assert.Contains("set \"ROOT=%~dp0\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set \"APP=%ROOT%artifacts\\publish\\win-x64\\LiveCaptionsTranslator.exe\"",
            script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if not exist \"%APP%\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echo ERROR: Published application was not found:", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Run publish-win-x64.bat first.", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit /b 1", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pushd \"%ROOT%\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start ", script, StringComparison.OrdinalIgnoreCase);
        int launchCommand = script.IndexOf("\"%APP%\"\r\n", StringComparison.OrdinalIgnoreCase);
        Assert.True(launchCommand >= 0, "The launcher should invoke the executable directly and wait for it.");
        Assert.Contains("set \"APP_EXIT=%ERRORLEVEL%\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("popd", script, StringComparison.OrdinalIgnoreCase);
        int captureExitCode = script.IndexOf("set \"APP_EXIT=%ERRORLEVEL%\"", StringComparison.OrdinalIgnoreCase);
        int returnExitCode = script.IndexOf("exit /b %APP_EXIT%", StringComparison.OrdinalIgnoreCase);
        Assert.True(launchCommand < captureExitCode && captureExitCode < returnExitCode,
            "The launcher must capture and return the executable's exit code after it exits.");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "run-win-x64.bat")) &&
                File.Exists(Path.Combine(directory.FullName, "LiveCaptionsTranslator.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
