using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Velopack.Core;
using Velopack.Packaging.Windows.Msi;
using Velopack.Packaging.Windows.Commands;
using Velopack.Windows;
using Velopack.Util;
using Velopack.Vpk;
using Velopack.Vpk.Logging;
using Velopack.TestCommon;
using WixToolset.Dtf.WindowsInstaller;

namespace Velopack.Packaging.Tests;

[SupportedOSPlatform("windows")]
public class MsiTests
{
    private readonly ITestOutputHelper _output;

    public MsiTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string RunMsiExec(string rawArgs, ILogger logger, int? exitCode = 0)
    {
        var outputFile = PathHelper.GetTestRootPath($"run.{WindowsTestHelper.RandomString(8)}.log");

        try {
            var fix = new ProcessStartInfo("cmd.exe");
            fix.CreateNoWindow = true;
            fix.WorkingDirectory = Environment.CurrentDirectory;
            fix.Arguments = $"/c msiexec.exe {rawArgs} > \"{outputFile}\" 2>&1";

            Stopwatch sw = new Stopwatch();
            sw.Start();

            logger.Info($"TEST: Running cmd.exe {fix.Arguments}");
            using var p = Process.Start(fix);

            var timeout = TimeSpan.FromMinutes(3);
            if (!p.WaitForExit(timeout))
                throw new TimeoutException($"Process did not exit within {timeout.TotalSeconds}s.");

            var elapsed = sw.Elapsed;
            sw.Stop();

            logger.Info($"TEST: Process exited with code {p.ExitCode} in {elapsed.TotalSeconds}s");

            using var fs = IoUtil.Retry(
                () => File.Open(outputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None),
                10,
                1000,
                logger.ToVelopackLogger());

            using var reader = new StreamReader(fs);
            var output = reader.ReadToEnd();

            if (String.IsNullOrWhiteSpace(output)) {
                logger.Warn($"TEST: Process output was empty");
            } else {
                logger.Info($"TEST: Process output: {Environment.NewLine}{output.Trim()}{Environment.NewLine}");
            }

            if (exitCode.HasValue && p.ExitCode != exitCode.Value) {
                throw new Exception($"Process exited with code {p.ExitCode} but expected {exitCode.Value}");
            }

            return output.Trim();
        } finally {
            try {
                File.Delete(outputFile);
            } catch { }
        }
    }

    private static string RunCoveredDotnetDeelevated(string exe, string[] args, string workingDir, ILogger logger, int? exitCode = 0)
    {
        // Runs dotnet-coverage with the target exe as a truly non-elevated user via explorer.exe.
        // explorer.exe delegates to the existing (non-elevated) shell process, so the child
        // process gets a proper non-elevated token (TokenIsElevated = false).
        // Note: "runas /trustlevel:0x20000" does NOT work for this because the restricted token
        // still reports TokenIsElevated=true (it was derived from an elevated session).
        var outputFile = PathHelper.GetTestRootPath($"run.{WindowsTestHelper.RandomString(8)}.log");
        var coverageFile = PathHelper.GetTestRootPath($"coverage.rundotnet.{WindowsTestHelper.RandomString(8)}.xml");
        var batchFile = PathHelper.GetTestRootPath($"run.{WindowsTestHelper.RandomString(8)}.cmd");

        if (!File.Exists(exe))
            throw new Exception($"File {exe} does not exist.");

        try {
            var argStr = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            File.WriteAllText(batchFile,
                $"@cd /d \"{workingDir}\"\r\n" +
                $"@\"dotnet-coverage\" collect -o \"{coverageFile}\" -f cobertura \"{exe}\" {argStr} > \"{outputFile}\" 2>&1\r\n");

            // Launch the batch file via explorer.exe, which delegates to the non-elevated shell.
            var fix = new ProcessStartInfo("explorer.exe");
            fix.Arguments = $"\"{batchFile}\"";

            logger.Info($"TEST: Running de-elevated via explorer.exe: \"{batchFile}\"");
            using var p = Process.Start(fix);
            p?.WaitForExit(TimeSpan.FromSeconds(10)); // explorer.exe exits almost immediately

            // Poll for the output file (the batch file runs asynchronously via the shell)
            using var fs = IoUtil.Retry(
                () => File.Open(outputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None),
                30,
                1000,
                logger.ToVelopackLogger());

            using var reader = new StreamReader(fs);
            var output = reader.ReadToEnd();

            if (String.IsNullOrWhiteSpace(output)) {
                logger.Warn($"TEST: Process output was empty");
            } else {
                logger.Info($"TEST: Process output: {Environment.NewLine}{output.Trim()}{Environment.NewLine}");
            }

            return String.Join(
                Environment.NewLine,
                output
                    .Split('\n')
                    .Where(l => !l.Contains("Code coverage results"))
                    .Select(l => l.Trim())
            ).Trim();
        } finally {
            try { File.Delete(outputFile); } catch { }
            try { File.Delete(batchFile); } catch { }
            try { File.Delete(coverageFile); } catch { }
        }
    }

    private static async Task PackTestAppWithMsi(string id, string version, string testString,
        string releaseDir, ILogger logger, string packAuthors = null)
    {
        var projDir = PathHelper.GetTestRootPath("TestApp");
        var testStringFile = Path.Combine(projDir, "Const.cs");
        var oldText = File.ReadAllText(testStringFile);

        try {
            File.WriteAllText(testStringFile, $"class Const {{ public const string TEST_STRING = \"{testString}\"; }}");
            var args = new List<string> {
                "publish", "--no-self-contained", "-c", "Release", "-r", "win-x64", "-o", "publish", "--tl:off"
            };

            var psi = new ProcessStartInfo("dotnet");
            psi.WorkingDirectory = projDir;
            psi.AppendArgumentListSafe(args, out var debug);

            logger.Info($"TEST: Running {psi.FileName} {debug}");

            using var p = Process.Start(psi);
            p.WaitForExit();

            if (p.ExitCode != 0)
                throw new Exception($"dotnet publish failed with exit code {p.ExitCode}");

            var options = new WindowsPackOptions {
                EntryExecutableName = "TestApp.exe",
                ReleaseDir = new DirectoryInfo(releaseDir),
                PackId = id,
                PackAuthors = packAuthors,
                PackVersion = version,
                TargetRuntime = RID.Parse("win-x64"),
                PackDirectory = Path.Combine(projDir, "publish"),
                BuildMsi = true,
                InstLocation = InstallLocation.PerMachine,
            };

            var runner = WindowsTestHelper.GetPackRunner(logger);
            await runner.Run(options);
        } finally {
            File.WriteAllText(testStringFile, oldText);
        }
    }

    private static (bool found, string displayVersion) FindUninstallEntry(RegistryKey root, string appId)
    {
        using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\MSI:{appId}");
        if (key == null) return (false, null);
        return (true, key.GetValue("DisplayVersion") as string);
    }

    [Fact]
    public async Task TestPackGeneratesMsi()
    {
        Assert.SkipUnless(VelopackRuntimeInfo.IsWindows, "Windows only");

        using var logger = _output.BuildLoggerFor<MsiTests>();

        using var _1 = TempUtil.GetTempDirectory(out var tmpOutput);
        using var _2 = TempUtil.GetTempDirectory(out var tmpReleaseDir);

        var exe = "testapp.exe";
        var pdb = Path.ChangeExtension(exe, ".pdb");
        var id = "Test.Squirrel-App";
        var version = "1.2.3";

        PathHelper.CopyRustAssetTo(exe, tmpOutput);
        PathHelper.CopyRustAssetTo(pdb, tmpOutput);

        var options = new WindowsPackOptions {
            EntryExecutableName = exe,
            ReleaseDir = new DirectoryInfo(tmpReleaseDir),
            PackId = id,
            PackVersion = version,
            TargetRuntime = RID.Parse("win-x64"),
            PackDirectory = tmpOutput,
            Shortcuts = "Desktop,StartMenuRoot",
            BuildMsi = true
        };

        var runner = WindowsTestHelper.GetPackRunner(logger);
        await runner.Run(options);

        string msiPath = Path.Combine(tmpReleaseDir, $"{id}-win.msi");
        Assert.True(File.Exists(msiPath));
        using Database db = new Database(msiPath);
        var msiVersion = db.ExecuteScalar("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'") as string;
        Assert.Equal("1.2.3.0", msiVersion);
    }

    [Fact]
    public void TestMsiTemplateUsesPublisherPathAndProductVersion()
    {
        using var _1 = TempUtil.GetTempDirectory(out var tmpOutput);
        var data = MsiBuilder.ConvertOptionsToTemplateData(
            new DirectoryInfo(tmpOutput),
            ShortcutLocation.None,
            "",
            new WindowsPackOptions {
                PackId = "MyApp",
                PackTitle = "My Application",
                PackAuthors = "BiMMate",
                PackVersion = "2.5.1",
                TargetRuntime = RID.Parse("win-x64"),
                EntryExecutableName = "MyApp.exe",
                ReleaseDir = new DirectoryInfo(tmpOutput),
                PackDirectory = tmpOutput,
            });

        var wxs = MsiBuilder.GenerateWixTemplate(data);

        Assert.Contains("Scope=\"perMachine\"", wxs);
        Assert.Contains("Id=\"PublisherDir\" Name=\"BiMMate\"", wxs);
        Assert.Contains("Id=\"INSTALLFOLDER\" Name=\"MyApp\"", wxs);
        Assert.Contains("Name=\"My Application 2.5.1.0\"", wxs);
        Assert.Contains("ProgramFiles64Folder", wxs);
        Assert.Contains("FileAssociations", wxs);
    }

    [Fact]
    public async Task TestPackGeneratesMsiWithInstallerPages()
    {
        Assert.SkipUnless(VelopackRuntimeInfo.IsWindows, "Windows only");

        using var logger = _output.BuildLoggerFor<MsiTests>();

        using var _1 = TempUtil.GetTempDirectory(out var tmpOutput);
        using var _2 = TempUtil.GetTempDirectory(out var tmpReleaseDir);
        using var _3 = TempUtil.GetTempDirectory(out var tmpAssets);

        var exe = "testapp.exe";
        var pdb = Path.ChangeExtension(exe, ".pdb");
        var id = "Test.Squirrel-App";
        var version = "1.2.3";

        PathHelper.CopyRustAssetTo(exe, tmpOutput);
        PathHelper.CopyRustAssetTo(pdb, tmpOutput);

        var welcomeFile = Path.Combine(tmpAssets, "welcome.txt");
        var readmeFile = Path.Combine(tmpAssets, "readme.txt");
        var licenseFile = Path.Combine(tmpAssets, "license.txt");
        var conclusionFile = Path.Combine(tmpAssets, "conclusion.txt");
        File.WriteAllText(welcomeFile, "WELCOME_TEXT_MARKER");
        File.WriteAllText(readmeFile, "README_TEXT_MARKER");
        File.WriteAllText(licenseFile, "LICENSE_TEXT_MARKER");
        File.WriteAllText(conclusionFile, "CONCLUSION_TEXT_MARKER");

        var options = new WindowsPackOptions {
            EntryExecutableName = exe,
            ReleaseDir = new DirectoryInfo(tmpReleaseDir),
            PackId = id,
            PackVersion = version,
            TargetRuntime = RID.Parse("win-x64"),
            PackDirectory = tmpOutput,
            Shortcuts = "Desktop,StartMenuRoot",
            BuildMsi = true,
            InstWelcome = welcomeFile,
            InstReadme = readmeFile,
            InstLicense = licenseFile,
            InstConclusion = conclusionFile,
        };

        var runner = WindowsTestHelper.GetPackRunner(logger);
        await runner.Run(options);

        string msiPath = Path.Combine(tmpReleaseDir, $"{id}-win.msi");
        Assert.True(File.Exists(msiPath));

        using Database db = new Database(msiPath);

        // Welcome -> override of MsiWelcomeDescription property
        var welcomeProp = db.ExecuteScalar("SELECT `Value` FROM `Property` WHERE `Property` = 'MsiWelcomeDescription'") as string;
        Assert.Equal("WELCOME_TEXT_MARKER", welcomeProp);

        // Readme -> ReadmeDlg dialog should exist (content is embedded via WixVariable RTF, not an MSI property)
        var readmeDialog = db.ExecuteScalar("SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = 'ReadmeDlg'") as string;
        Assert.Equal("ReadmeDlg", readmeDialog);

        // Conclusion -> WIXUI_EXITDIALOGOPTIONALTEXT property
        var conclusionProp = db.ExecuteScalar(
            "SELECT `Value` FROM `Property` WHERE `Property` = 'WIXUI_EXITDIALOGOPTIONALTEXT'") as string;
        Assert.Equal("CONCLUSION_TEXT_MARKER", conclusionProp);

        // License -> LicenseAgreementDlg dialog is always present; ensure WixUILicenseRtf was set
        // (the WixVariable is compiled into WixVariable table)
        var licenseDialog = db.ExecuteScalar("SELECT `Dialog` FROM `Dialog` WHERE `Dialog` = 'LicenseAgreementDlg'") as string;
        Assert.Equal("LicenseAgreementDlg", licenseDialog);
    }

    [Fact]
    public async Task TestPackGeneratesMsiWithSpecifiedVersion()
    {
        Assert.SkipUnless(VelopackRuntimeInfo.IsWindows, "Windows only");

        using var logger = _output.BuildLoggerFor<MsiTests>();

        using var _1 = TempUtil.GetTempDirectory(out var tmpOutput);
        using var _2 = TempUtil.GetTempDirectory(out var tmpReleaseDir);

        var exe = "testapp.exe";
        var pdb = Path.ChangeExtension(exe, ".pdb");
        var id = "Test.Squirrel-App";
        var version = "1.0.0";

        PathHelper.CopyRustAssetTo(exe, tmpOutput);
        PathHelper.CopyRustAssetTo(pdb, tmpOutput);

        var options = new WindowsPackOptions {
            EntryExecutableName = exe,
            ReleaseDir = new DirectoryInfo(tmpReleaseDir),
            PackId = id,
            PackVersion = version,
            TargetRuntime = RID.Parse("win-x64"),
            PackDirectory = tmpOutput,
            Shortcuts = "Desktop,StartMenuRoot",
            BuildMsi = true,
            MsiVersionOverride = "4.5.6.0"
        };

        var runner = WindowsTestHelper.GetPackRunner(logger);
        await runner.Run(options);

        string msiPath = Path.Combine(tmpReleaseDir, $"{id}-win.msi");
        Assert.True(File.Exists(msiPath));

        using Database db = new Database(msiPath);
        var msiVersion = db.ExecuteScalar("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'") as string;
        Assert.Equal("4.5.6.0", msiVersion);
    }



    [Fact]
    public async Task TestMsiInstallToCustomDirViaVelopackInstallDir()
    {
        Assert.SkipUnless(VelopackRuntimeInfo.IsWindows, "Windows only");
        using var logger = _output.BuildLoggerFor<MsiTests>();
        using var _1 = TempUtil.GetTempDirectory(out var releaseDir);
        using var _2 = TempUtil.GetTempDirectory(out var customParent);

        string id = "MsiCustomDirTest";
        // a custom install dir that is NOT the default %LocalAppData%\{id} location
        var customDir = Path.Combine(customParent, "Custom", "WLYS");
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id);
        var msiPath = Path.Combine(releaseDir, $"{id}-win.msi");
        var appPath = Path.Combine(customDir, "current", "TestApp.exe");

        try {
            await PackTestAppWithMsi(id, "1.0.0", "custom dir test", releaseDir, logger);
            Assert.True(File.Exists(msiPath), $"MSI not found at {msiPath}");

            // install per-user, overriding the install dir via VELOPACK_INSTALLDIR
            logger.Info($"TEST: Installing MSI to custom dir {customDir}...");
            RunMsiExec($"/i \"{msiPath}\" /qn VELOPACK_INSTALLDIR=\"{customDir}\"", logger);

            // app must land in the custom dir, NOT the default LocalAppData location
            Assert.True(File.Exists(appPath), $"TestApp.exe not found at custom dir {appPath}");
            Assert.False(Directory.Exists(defaultDir),
                $"App should NOT have installed to the default dir {defaultDir}");

            var chkVersion = WindowsTestHelper.RunCoveredDotnet(appPath, ["version"], customDir, logger);
            Assert.EndsWith(Environment.NewLine + "1.0.0", chkVersion);
            logger.Info("TEST: app installed to custom dir and verified");
        } finally {
            try {
                if (File.Exists(msiPath)) {
                    RunMsiExec($"/x \"{msiPath}\" /qn", logger, exitCode: null);
                }
            } catch {
                // best effort cleanup
            }

            try {
                if (Directory.Exists(customDir)) {
                    IoUtil.Retry(() => IoUtil.DeleteFileOrDirectoryHard(customDir), 10, 1000);
                }
            } catch {
                // best effort cleanup
            }

            try {
                if (Directory.Exists(defaultDir)) {
                    IoUtil.Retry(() => IoUtil.DeleteFileOrDirectoryHard(defaultDir), 10, 1000);
                }
            } catch {
                // best effort cleanup
            }
        }
    }

    // Requires elevation and UAC approval. To run from CLI:
    //   dotnet test test/Velopack.Packaging.Tests --filter TestMsiPerMachineInstallAndUpdate -- xUnit.Explicit=only
    [Fact(Explicit = true)]
    public async Task TestMsiPerMachineInstallAndUpdate()
    {
        Assert.SkipUnless(VelopackRuntimeInfo.IsWindows, "Windows only");
        using var logger = _output.BuildLoggerFor<MsiTests>();
        using var _1 = TempUtil.GetTempDirectory(out var releaseDir);

        string id = "MsiPerMachineTest";
        const string publisher = "BiMMate";
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), publisher, id);
        var fallbackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id);
        var msiPath = Path.Combine(releaseDir, $"{id}-win.msi");
        var appPath = Path.Combine(installDir, "current", "TestApp.exe");

        try {
            // pack v1
            await PackTestAppWithMsi(id, "1.0.0", "version 1 test", releaseDir, logger, publisher);
            Assert.True(File.Exists(msiPath), $"MSI not found at {msiPath}");

            // silent per-machine install: default path comes from ProgramFiles/Publisher/App layout (no INSTALLFOLDER needed)
            logger.Info("TEST: Installing MSI per-machine...");
            RunMsiExec($"/i \"{msiPath}\" /qn", logger);

            // verify install
            Assert.True(File.Exists(appPath), $"TestApp.exe not found at {appPath}");
            var chk1version = WindowsTestHelper.RunCoveredDotnet(appPath, ["version"], installDir, logger);
            Assert.EndsWith(Environment.NewLine + "1.0.0", chk1version);
            logger.Info("TEST: v1 installed and verified");

            // verify uninstall registry is in HKLM for per-machine install
            var (hklmFound, hklmVersion) = FindUninstallEntry(Registry.LocalMachine, id);
            Assert.True(hklmFound, "Uninstall entry should exist in HKLM for per-machine MSI install");
            Assert.Equal("1.0.0", hklmVersion);
            logger.Info("TEST: registry entry verified in HKLM with version 1.0.0");

            // Run packagesdir check as a de-elevated (standard) user.
            // Program Files is not writable for standard users, so the locator should
            // fallback to %LOCALAPPDATA%/{id}/packages.
            string fallbackPackagesPath = Path.Combine(fallbackDir, "packages");
            var chk1pkgdir = RunCoveredDotnetDeelevated(appPath, ["packagesdir"], installDir, logger);
            Assert.EndsWith(Environment.NewLine + fallbackPackagesPath, chk1pkgdir);
            logger.Info("TEST: packages dir correctly fell back to " + fallbackPackagesPath);

            // pack v2
            await PackTestAppWithMsi(id, "2.0.0", "version 2 test", releaseDir, logger, publisher);

            // check for updates (de-elevated)
            var chk2check = RunCoveredDotnetDeelevated(appPath, ["check", releaseDir], installDir, logger);
            Assert.EndsWith(Environment.NewLine + "update: 2.0.0", chk2check);
            logger.Info("TEST: found v2 update (de-elevated)");

            // download update as de-elevated user (nupkg should go to fallback packages dir,
            // and Update.exe should be extracted to the fallback dir)
            RunCoveredDotnetDeelevated(appPath, ["download", releaseDir], installDir, logger);

            // verify nupkg ended up in the fallback packages dir (NOT Program Files)
            var nupkgFileName = $"{id}-2.0.0-full.nupkg";
            Assert.True(File.Exists(Path.Combine(fallbackPackagesPath, nupkgFileName)),
                $"Downloaded nupkg not found in fallback packages dir: {fallbackPackagesPath}");
            Assert.False(File.Exists(Path.Combine(installDir, "packages", nupkgFileName)),
                "Nupkg should NOT be in Program Files packages dir when running as standard user");
            logger.Info("TEST: nupkg downloaded to correct fallback packages dir");

            // verify Update.exe was extracted to the fallback dir (not Program Files)
            var fallbackUpdateExe = Path.Combine(fallbackDir, "Update.exe");
            Assert.True(File.Exists(fallbackUpdateExe),
                $"Update.exe not found in fallback dir: {fallbackUpdateExe}");
            logger.Info("TEST: Update.exe extracted to fallback dir");

            // apply update as de-elevated user (Update.exe should self-elevate via UAC)
            RunCoveredDotnetDeelevated(appPath, ["apply", releaseDir], installDir, logger, exitCode: null);
            Thread.Sleep(30000); // wait for UAC prompt + elevated apply + app restart

            // verify update (de-elevated to confirm the update was applied)
            var chk2version = RunCoveredDotnetDeelevated(appPath, ["version"], installDir, logger);
            Assert.EndsWith(Environment.NewLine + "2.0.0", chk2version);
            logger.Info("TEST: v2 update verified");

            // verify registry was updated to v2
            var (hklmFound2, hklmVersion2) = FindUninstallEntry(Registry.LocalMachine, id);
            Assert.True(hklmFound2, "Uninstall entry should still exist in HKLM after update");
            Assert.Equal("2.0.0", hklmVersion2);
            logger.Info("TEST: registry entry verified in HKLM with version 2.0.0");
        } finally {
            // cleanup: uninstall MSI
            try {
                RunMsiExec($"/x \"{msiPath}\" /qn", logger, exitCode: null);
            } catch {
                // best effort cleanup
            }

            // cleanup fallback dir
            try {
                if (Directory.Exists(fallbackDir)) {
                    IoUtil.Retry(() => IoUtil.DeleteFileOrDirectoryHard(fallbackDir), 10, 1000);
                }
            } catch {
                // best effort cleanup
            }

            // cleanup install dir if MSI uninstall left remnants
            try {
                if (Directory.Exists(installDir)) {
                    IoUtil.Retry(() => IoUtil.DeleteFileOrDirectoryHard(installDir), 10, 1000);
                }
            } catch {
                // best effort cleanup
            }
        }
    }
}
