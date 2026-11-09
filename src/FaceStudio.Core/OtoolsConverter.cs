using System.Diagnostics;

namespace Cricket07.FaceStudio.Core;

public sealed class OtoolsConverter(string executable)
{
    public string Executable { get; } = Path.GetFullPath(executable);

    public async Task ConvertAsync(string operation, string input, string output, CancellationToken ct = default)
    {
        if (operation is not ("import" or "export" or "dump")) throw new ArgumentException("Invalid conversion operation.");
        if (!File.Exists(Executable)) throw new FileNotFoundException("Select the OTools executable in Settings.", Executable);
        var psi = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { operation, "-i", Path.GetFullPath(input), "-o", Path.GetFullPath(output), "-game", "cricket07", "-silent", "-console" }) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new IOException("Could not start OTools.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw new IOException(ct.IsCancellationRequested ? "Conversion cancelled." : "OTools timed out after 60 seconds.");
        }
        string log = await stdout + await stderr;
        if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException($"OTools {operation} failed (0x{process.ExitCode:X8}). {log}");
    }
}
