namespace XLinkSharp;

public enum XLinkGame { BotW, Totk }

public enum ModuleType { ELink, SLink }

public enum ParamType : uint { Int, Float, Bool, Enum, String, Custom }

public enum RefType : byte
{
    Direct, String, Curve, Random, ArrangeGroup, Bitflag,
    Random2Pow, Random3Pow, Random4Pow, Random1Point5Pow,
    Random2PowWeightMin, Random3PowWeightMin, Random4PowWeightMin, Random1Point5PowWeightMin,
    Random2PowWeightMax, Random3PowWeightMax, Random4PowWeightMax, Random1Point5PowWeightMax,
}

public enum ContainerType : byte { Switch, Random, Random2, Blend, Sequence, Grid, Jump }

public enum PropertyType : byte { Enum, S32, F32, Bool, S32Alt, F32Alt }

public enum CompareType : byte { Equal, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, NotEqual }

public enum BlendOp : byte { None, Multiply, SquareRoot, Sin, Add, SetToOne }

public enum LimitType : byte
{
    None, PriorityThenOldest, PriorityThenNewest, OldestThenPriority, NewestThenPriority,
    SpatialPriorityThenOldest, SpatialPriorityThenNewest, OldestThenSpatialPriority, NewestThenSpatialPriority,
}

public sealed record ParamDefine(string Name, ParamType Type, object? Default);

public sealed record CurvePoint(float X, float Y);

public sealed record RandomRange(float Min, float Max);

public sealed record ArrangeGroup(string Name, LimitType LimitType, sbyte Threshold, bool IncludeFading);

public sealed class Curve
{
    public ushort Type { get; set; }
    public bool IsGlobal { get; set; }
    public string PropertyName { get; set; } = "";
    public int Unknown { get; set; }
    public short UpdateType { get; set; }
    public List<CurvePoint> Points { get; } = [];
}

public sealed class ParamValue
{
    public RefType RefType { get; set; }
    public object? Value { get; set; }

    public ParamValue() { }
    public ParamValue(RefType type, object? value) { RefType = type; Value = value; }
}

public abstract class Condition
{
    public ContainerType ParentType { get; set; }
}

public sealed class SwitchCondition : Condition
{
    public PropertyType PropertyType { get; set; }
    public CompareType Compare { get; set; }
    public object Value { get; set; } = 0;
    public bool IsSolved { get; set; }
    public bool IsGlobal { get; set; }
    public bool IsAction { get; set; }
}

public sealed class RandomCondition : Condition
{
    public float Weight { get; set; }
}

public sealed class BlendCondition : Condition
{
    public float Min { get; set; }
    public float Max { get; set; }
    public BlendOp MinOp { get; set; }
    public BlendOp MaxOp { get; set; }
}

public sealed class SequenceCondition : Condition
{
    public int ForceContinue { get; set; }
}

public sealed class EmptyCondition : Condition;

public sealed class Container
{
    public ContainerType Type { get; set; }
    public bool IsBlendBy { get; set; }
    public int ChildrenStart { get; set; }
    public int ChildrenEnd { get; set; }
    public string? WatchPropertyName { get; set; }
    public int WatchPropertyId { get; set; }
    public bool IsGlobal { get; set; }
    public bool IsAction { get; set; }
    public string? Property1Name { get; set; }
    public string? Property2Name { get; set; }
    public bool IsProperty1Global { get; set; }
    public bool IsProperty2Global { get; set; }
    public List<string> Values1 { get; } = [];
    public List<string> Values2 { get; } = [];
    public List<int> Cells { get; } = [];
    public bool HasWatchProperty => Type == ContainerType.Switch || (Type == ContainerType.Blend && IsBlendBy);
}

public sealed class AssetCall
{
    public string KeyName { get; set; } = "";
    public ushort Flag { get; set; }
    public int Duration { get; set; }
    public int ParentIndex { get; set; } = -1;
    public uint Guid { get; set; }
    public List<KeyValuePair<string, ParamValue>>? Params { get; set; }
    public Container? Container { get; set; }
    public Condition? Condition { get; set; }
    public bool IsContainer => Container is not null;
}

public sealed class Trigger
{
    public uint Guid { get; set; }
    public int Unknown1 { get; set; }
    public int AssetCallIndex { get; set; }
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }
    public string? PreviousActionName { get; set; }
    public ushort Flag { get; set; }
    public ushort OverwriteHash { get; set; }
    public SwitchCondition? Condition { get; set; }
    public List<KeyValuePair<string, ParamValue>>? Overwrite { get; set; }
    public bool IsPrevious => (Flag & 0x10) != 0;
}

public sealed class Action
{
    public string Name { get; set; } = "";
    public bool IsPrefix { get; set; }
    public List<Trigger> Triggers { get; } = [];
}

public sealed class ActionSlot
{
    public string Name { get; set; } = "";
    public List<Action> Actions { get; } = [];
}

public sealed class Property
{
    public string WatchPropertyName { get; set; } = "";
    public bool IsGlobal { get; set; }
    public List<Trigger> Triggers { get; } = [];
}

public sealed class User
{
    public uint NameHash { get; set; }
    public string? Name { get; set; }
    public uint IsSetup { get; set; }
    public short Unknown { get; set; }
    public List<string> LocalProperties { get; } = [];
    public List<KeyValuePair<string, ParamValue>> Params { get; } = [];
    public List<AssetCall> AssetCalls { get; } = [];
    public List<ActionSlot> ActionSlots { get; } = [];
    public List<Property> Properties { get; } = [];
    public List<Trigger> AlwaysTriggers { get; } = [];

    public int NumAsset => AssetCalls.Count(a => !a.IsContainer);
    public int NumRandomContainer2 => AssetCalls.Count(a => a.Container?.Type == ContainerType.Random2);

    public ushort[] SortedAssetIds()
    {
        var keys = AssetCalls.Select(a => XLinkFile.Utf8.GetBytes(a.KeyName)).ToArray();
        return [.. Enumerable.Range(0, keys.Length).OrderBy(i => keys[i], ByteComparer.Instance).ThenBy(i => i).Select(i => (ushort)i)];
    }
}

public sealed class XLinkFile
{
    public XLinkGame Game { get; set; }
    public ModuleType Module { get; set; }
    public bool BigEndian { get; set; }
    public List<ParamDefine> UserParams { get; } = [];
    public List<ParamDefine> AssetParams { get; } = [];
    public List<ParamDefine> TriggerParams { get; } = [];
    public int NumCustomAssetParam { get; set; }
    public List<string> LocalPropertyNames { get; } = [];
    public List<string> LocalPropertyEnumNames { get; } = [];
    public List<User> Users { get; } = [];

    public uint Version => (Game, Module) switch
    {
        (XLinkGame.BotW, ModuleType.ELink) => 0x1E,
        (XLinkGame.BotW, ModuleType.SLink) => 0x1C,
        (XLinkGame.Totk, ModuleType.ELink) => 0x24,
        _ => 0x21,
    };

    internal static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false);

    public static XLinkFile FromBinary(ReadOnlySpan<byte> data) => new XLinkReader(data.ToArray()).Read();

    public static XLinkFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public byte[] ToBinary() => new XLinkWriter(this).Write();

    public void WriteTo(string path) => File.WriteAllBytes(path, ToBinary());

    public static uint Crc32(string s)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in Utf8.GetBytes(s))
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320 & (uint)-(int)(c & 1));
        }
        return ~c;
    }
}

sealed class ByteComparer : IComparer<byte[]>
{
    public static readonly ByteComparer Instance = new();
    public int Compare(byte[]? a, byte[]? b) => a.AsSpan().SequenceCompareTo(b);
}
