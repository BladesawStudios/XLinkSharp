namespace XLinkSharp.Tests;

public class TextTests
{
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.txt"));

    [Theory]
    [InlineData(0.0f, "0.0")]
    [InlineData(1.0f, "1.0")]
    [InlineData(-1.0f, "-1.0")]
    [InlineData(0.5f, "0.5")]
    [InlineData(0.1f, "0.100000001")]
    [InlineData(-1.5f, "-1.5")]
    [InlineData(100000000.0f, "100000000.0")]
    [InlineData(123456789.5f, "123456792.0")]
    [InlineData(1e-5f, "9.99999975e-06")]
    [InlineData(1.0e-4f, "9.99999975e-05")]
    [InlineData(1.17549435e-38f, "1.17549435e-38")]
    [InlineData(12345.678f, "12345.6777")]
    [InlineData(1234567.9f, "1234567.88")]
    [InlineData(0.00012345678f, "0.000123456775")]
    [InlineData(3.4028235e38f, "340282346638528859811704183484516925440.0")]
    public void FloatsPrintTheWayTheToolPrintsThem(float value, string expected)
        => Assert.Equal(expected, TextFormat.Float(value));

    [Fact]
    public void NegativeZeroKeepsItsSign() => Assert.Equal("-0.0", TextFormat.Float(-0.0f));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("a=b", "\"a=b\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("quote\"d", "\"quote\\\"d\"")]
    [InlineData("a:b", "\"a:b\"")]
    [InlineData("100%", "100%")]
    public void NamesAreQuotedOnlyWhenNeeded(string name, string expected)
        => Assert.Equal(expected, TextFormat.Quoted(name));

    [Fact]
    public void TheSampleParsesAndPrintsUnchanged()
    {
        XLinkFile file = XLinkFile.FromText(Sample);

        Assert.Equal(ModuleType.ELink, file.Module);
        Assert.Equal(XLinkGame.Totk, file.Game);
        Assert.Equal(Sample, file.ToText());
    }

    [Fact]
    public void TheSampleSurvivesBinary()
    {
        XLinkFile file = XLinkFile.FromText(Sample);

        XLinkFile again = XLinkFile.FromBinary(file.ToBinary());

        Assert.Equal(Sample, again.ToText());
    }

    [Fact]
    public void TheSampleHasEveryContainerKind()
    {
        XLinkFile file = XLinkFile.FromText(Sample);
        User user = Assert.Single(file.Users);

        Assert.Equal("Demo", user.Name);
        Assert.Equal(XLinkFile.Crc32("Demo"), user.NameHash);
        var kinds = user.AssetCalls.Where(a => a.Container is not null).Select(a => (a.Container!.Type, a.Container.IsBlendBy)).ToHashSet();
        foreach (var kind in new[] { ContainerType.Switch, ContainerType.Random, ContainerType.Sequence, ContainerType.Blend, ContainerType.Grid, ContainerType.Jump })
            Assert.Contains(kinds, k => k.Type == kind);
        Assert.Contains((ContainerType.Blend, true), kinds);
    }

    [Fact]
    public void ChildrenOfAContainerAreNextToEachOther()
    {
        User user = XLinkFile.FromText(Sample).Users[0];

        foreach (var container in user.AssetCalls.Select(a => a.Container).Where(c => c is { Type: not (ContainerType.Jump) }))
        {
            Assert.True(container!.ChildrenStart >= 0);
            Assert.True(container.ChildrenEnd >= container.ChildrenStart);
        }
    }

    [Fact]
    public void ParametersAreFilledInInTheOrderOfTheirDefines()
    {
        User user = XLinkFile.FromText(Sample).Users[0];

        Assert.Equal(["Radius", "Tag", "Level"], user.Params.Select(p => p.Key));

        string withoutParams = Sample.Replace("      Level = 7\n", "");
        User filled = XLinkFile.FromText(withoutParams).Users[0];

        Assert.Equal(0, filled.Params[2].Value.Value);
    }

    [Fact]
    public void ReferencesNeedNoBodyInTriggersAndJumps()
    {
        XLinkFile file = XLinkFile.FromText(Sample);

        User user = file.Users[0];
        Assert.Equal("Loop", user.AssetCalls[user.ActionSlots[0].Actions[0].Triggers[0].AssetCallIndex].KeyName);
        Assert.Equal("Burst", user.AssetCalls[user.AlwaysTriggers[0].AssetCallIndex].KeyName);
        var jump = user.AssetCalls.Single(a => a.Container?.Type == ContainerType.Jump);
        Assert.Equal("Cell1", user.AssetCalls[jump.Container!.ChildrenStart].KeyName);
    }

    [Fact]
    public void AnEmptyActionRangeIsEmptyAndSurvivesBinary()
    {
        XLinkFile again = XLinkFile.FromBinary(XLinkFile.FromText(Sample).ToBinary());

        var slot = again.Users[0].ActionSlots[0];
        Assert.Equal(["Walk", "Run"], slot.Actions.Select(a => a.Name));
        Assert.Empty(slot.Actions[1].Triggers);
    }

    [Fact]
    public void NamesWithOddCharactersSurvive()
    {
        string text = Sample.Replace("fx_loop", "full\\u0001 wide\\\\ \\\"　x");

        string printed = XLinkFile.FromText(text).ToText();
        string again = XLinkFile.FromText(printed).ToText();

        Assert.Equal(printed, again);
        Assert.Contains("\\u0001", printed);
    }

    [Fact]
    public void CommentsAreSkipped()
    {
        Assert.Equal(Sample, XLinkFile.FromText("# a note\n" + Sample.Replace("Metadata {", "Metadata { # trailing")).ToText());
    }

    [Theory]
    [InlineData("      Level = 7\n", "      Level = 7\n      Colour = 3\n", "Unknown param 'Colour'")]
    [InlineData("Loop[0x00000003] {", "Loop[0x00000005] {", "defined twice")]
    [InlineData("Asset = Burst[0x00000005]\n      }\n    }\n    AssetCallTables", "Asset = Burst[0x00000063]\n      }\n    }\n    AssetCallTables", "0x00000063")]
    [InlineData("Execute = Random {", "Execute = Colour {", "unknown call table type")]
    public void MistakesAreReportedClearly(string find, string replace, string expected)
    {
        string broken = Sample.Replace(find, replace);
        Assert.NotEqual(Sample, broken);

        var error = Assert.Throws<FormatException>(() => XLinkFile.FromText(broken).ToBinary());

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ErrorsNameTheLine()
    {
        var error = Assert.Throws<FormatException>(() => XLinkFile.FromText(Sample.Replace("ModuleType = ELink", "ModuleType = Other")));

        Assert.Contains("Line 2", error.Message);
    }

    [Fact]
    public void AGridNeedsACaseForEveryChildItNames()
    {
        string broken = Sample.Replace("=> Cell2[0x00000013]\n", "=> Missing[0x000000ff]\n");

        var error = Assert.Throws<FormatException>(() => XLinkFile.FromText(broken));

        Assert.Contains("0x000000ff", error.Message);
    }

    [Fact]
    public void ABotwFileTakesItsLayoutFromTheGame()
    {
        // BotW has no grid, jump or BlendBy containers, so keep the sample to what that game has.
        string text = """
            Metadata {
              ModuleType = ELink
            }
            ParamDefines {
              SystemUserParams {
              }
              CustomUserParams {
              }
              SystemAssetParams {
                AssetName = ""
              }
              CustomAssetParams {
              }
              TriggerParams {
              }
            }
            Users {
              Link {
                Unknown = -1
                UserParams {
                }
                AlwaysTriggers {
                  0x00000002 {
                    Flags = 0
                    Unknown = 0
                    Asset = Fx[0x00000001]
                  }
                }
                AssetCallTables {
                  Fx[0x00000001] {
                    EmitCount = 1
                    Oneshot = false
                    NoPause = false
                    UserFlags = 0b0
                    Execute = Asset {
                      AssetName = "glow"
                    }
                  }
                }
              }
            }

            """.Replace("\r\n", "\n");

        XLinkFile file = XLinkFile.FromText(text, XLinkGame.BotW);
        byte[] binary = file.ToBinary();

        Assert.Equal(0x1Eu, BitConverter.ToUInt32(binary, 8));
        Assert.Equal(text, XLinkFile.FromBinary(binary).ToText());
    }
}
