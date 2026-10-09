using System.Diagnostics;
using SarcSharp;
using Yaz0Sharp;
using ZsDicSharp;

namespace XLinkSharp.Tests;

// Runs over the real databases when TOTK_ROMFS / BOTW_ROMFS point at a dump. When XLINK_TOOL names the xlink2
// executable (xlink.exe), the text is also compared with the tool's own, which is the oracle for the text form.
public class CorpusTests
{
    private static string? Totk
        => Environment.GetEnvironmentVariable("TOTK_ROMFS") is { } root && Directory.Exists(Path.Combine(root, "ELink2")) ? root : null;

    private static string? Botw
        => Environment.GetEnvironmentVariable("BOTW_ROMFS") is { } root && File.Exists(Path.Combine(root, "Pack", "Bootup.pack")) ? root : null;

    private static string? Tool
        => Environment.GetEnvironmentVariable("XLINK_TOOL") is { } path && File.Exists(path) ? path : null;

    private static byte[] TotkFile(string root, string dir, string name)
    {
        using ZsDic zs = ZsDic.FromRomfs(root);
        return zs.DecompressFile(Path.Combine(root, dir, name));
    }

    private static byte[] BotwFile(string root, string entry)
    {
        byte[] data = SarcFile.FromBinary(File.ReadAllBytes(Path.Combine(root, "Pack", "Bootup.pack"))).Entries.Single(e => e.Name == entry).Data;
        return Yaz0.DecompressIfNeeded(data);
    }

    public static IEnumerable<object[]> Databases()
    {
        yield return new object[] { "totk", "ELink2", "elink2.Product.110.belnk.zs", XLinkGame.Totk, "EXKing" };
        yield return new object[] { "totk", "SLink2", "slink2.Product.110.bslnk.zs", XLinkGame.Totk, "EXKing" };
        yield return new object[] { "botw", "ELink2/ELink2DB.sbelnk", "", XLinkGame.BotW, "UKing" };
        yield return new object[] { "botw", "SLink2/SLink2DB.sbslnk", "", XLinkGame.BotW, "UKing" };
    }

    private static byte[]? Load(string source, string a, string b)
    {
        if (source == "totk") return Totk is { } t ? TotkFile(t, a, b) : null;
        return Botw is { } w ? BotwFile(w, a) : null;
    }

    private static string RunTool(string tool, string input, string output, string inType, string outType, string game)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in new[] { "-i", input, "-o", output, "-it", inType, "-ot", outType, "-g", game, "-p", "NX" }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(output), $"xlink failed: {errors}");
        return File.ReadAllText(output);
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public void TextRoundTripsThroughTextAndBinary(string source, string a, string b, XLinkGame game, string toolGame)
    {
        if (Load(source, a, b) is not { } bytes) return;

        XLinkFile file = XLinkFile.FromBinary(bytes);
        Assert.Equal(game, file.Game);
        string text = file.ToText();

        Assert.Equal(text, XLinkFile.FromText(text, game).ToText());
        Assert.Equal(text, XLinkFile.FromBinary(XLinkFile.FromText(text, game).ToBinary()).ToText());
        Assert.Equal(text, XLinkFile.FromBinary(file.ToBinary()).ToText());
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public void TextIsTheSameAsTheToolsAndTheToolReadsOurBinary(string source, string a, string b, XLinkGame game, string toolGame)
    {
        if (Tool is not { } tool || Load(source, a, b) is not { } bytes) return;

        string dir = Path.Combine(Path.GetTempPath(), "xlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "original.bin");
            File.WriteAllBytes(original, bytes);
            string toolText = RunTool(tool, original, Path.Combine(dir, "tool.txt"), "binary", "text", toolGame);

            XLinkFile file = XLinkFile.FromBinary(bytes);
            Assert.True(toolText == file.ToText(), "The text differs from the tool's.");

            string ours = Path.Combine(dir, "ours.bin");
            File.WriteAllBytes(ours, XLinkFile.FromText(toolText, game).ToBinary());
            string reread = RunTool(tool, ours, Path.Combine(dir, "reread.txt"), "binary", "text", toolGame);
            Assert.True(toolText == reread, "The tool reads our binary as different text.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
