using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;

namespace SonosStreaming.App.Services;

public sealed class DiagnosticsPackageService
{
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RoomRelay");

    public static string DiagnosticsDir => Path.Combine(AppDataDir, "Diagnostics");

    public static string VersionLabel
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticsPackageService).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(info) ? assembly.GetName().Version?.ToString() ?? "unknown" : info;
        }
    }

    public string CreatePackage(AppCore core, PipelineRunner pipeline, string? lastError)
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(DiagnosticsDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(DiagnosticsDir, $"RoomRelay-Diagnostics-{timestamp}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddLogs(archive);
        AddFileIfExists(archive, Path.Combine(AppDataDir, "crash.txt"), "crash.txt");
        AddText(archive, "diagnostics.txt", BuildDiagnostics(core, pipeline, lastError));

        return zipPath;
    }

    public void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppDataDir);
        Process.Start(new ProcessStartInfo(AppDataDir) { UseShellExecute = true });
    }

    public void OpenDiagnosticsPackage(string packagePath)
    {
        if (!File.Exists(packagePath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{packagePath}\"") { UseShellExecute = true });
    }

    private static void AddLogs(ZipArchive archive)
    {
        if (!Directory.Exists(AppDataDir)) return;

        foreach (var path in Directory.EnumerateFiles(AppDataDir, "app*.log"))
            AddFileIfExists(archive, path, Path.Combine("logs", Path.GetFileName(path)));
    }

    private static void AddFileIfExists(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path)) return;
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void AddText(ZipArchive archive, string entryName, string contents)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }

    private static string BuildDiagnostics(AppCore core, PipelineRunner pipeline, string? lastError)
    {
        var selection = core.Selection;
        var sb = new StringBuilder();
        sb.AppendLine("RoomRelay Diagnostics");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Version: {VersionLabel}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Process: {RuntimeInformation.ProcessArchitecture}, 64-bit={Environment.Is64BitProcess}");
        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine("Current State");
        sb.AppendLine($"State: {core.State}");
        sb.AppendLine($"Source: {selection.Source}");
        sb.AppendLine($"Process: {selection.ProcessSelection?.Name ?? "n/a"} ({selection.ProcessSelection?.Pid.ToString() ?? "n/a"})");
        sb.AppendLine($"Format: {pipeline.Format}");
        sb.AppendLine($"Selected speaker: {selection.Speaker?.FriendlyName ?? "n/a"}");
        sb.AppendLine($"Selected speaker UDN: {selection.Speaker?.Udn ?? "n/a"}");
        sb.AppendLine($"Discovered speakers: {selection.Discovered.Count}");
        sb.AppendLine($"Clients: {pipeline.ClientCount}");
        sb.AppendLine($"Last UI error: {lastError ?? "n/a"}");
        sb.AppendLine();
        sb.AppendLine("Network Adapters");

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            sb.AppendLine($"- {ni.Name} ({ni.NetworkInterfaceType})");
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                sb.AppendLine($"  {ua.Address}");
        }

        sb.AppendLine();
        sb.AppendLine("Privacy note: logs and diagnostics may include local IP addresses, Sonos room names, device UDNs, and process names.");
        return sb.ToString();
    }
}
