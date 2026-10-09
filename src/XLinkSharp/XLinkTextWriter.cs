using System.Globalization;
using System.Text;

namespace XLinkSharp;

/// <summary>Formatting helpers shared by the text writer and parser; they follow the xlink2 tool's text exactly.</summary>
static class TextFormat
{
    public static string Hex(uint v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);
    public static string Hex8(uint v) => "0x" + v.ToString("x8", CultureInfo.InvariantCulture);
    public static string Bin(uint v) => "0b" + Convert.ToString(v, 2);
    public static string Bool(bool v) => v ? "true" : "false";

    /// <summary>A float the way the tool prints it: whole numbers as "1.0", others with 9 significant digits.</summary>
    public static string Float(float v)
    {
        if (!float.IsFinite(v)) return float.IsNaN(v) ? (float.IsNegative(v) ? "-nan" : "nan") : (v < 0 ? "-inf" : "inf");
        if (MathF.Round(v, MidpointRounding.AwayFromZero) == v) return ((double)v).ToString("F1", CultureInfo.InvariantCulture);
        return General9(v);
    }

    // C's %.9g: nine significant digits, scientific when the exponent is below -4 or at least 9, trailing zeros removed.
    static string General9(float v)
    {
        string e = ((double)v).ToString("E8", CultureInfo.InvariantCulture); // -d.dddddddde+XXX
        bool neg = e[0] == '-';
        if (neg) e = e[1..];
        int ePos = e.IndexOf('E');
        int exp = int.Parse(e[(ePos + 1)..], CultureInfo.InvariantCulture);
        string digits = e[..ePos].Replace(".", "");
        string body;
        if (exp < -4 || exp >= 9)
        {
            string mant = digits.TrimEnd('0');
            if (mant.Length == 0) mant = "0";
            body = mant.Length > 1 ? mant[0] + "." + mant[1..] : mant;
            body += "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (exp >= 0)
        {
            string whole = digits[..(exp + 1)];
            string frac = digits[(exp + 1)..].TrimEnd('0');
            body = frac.Length > 0 ? whole + "." + frac : whole;
        }
        else
        {
            string frac = (new string('0', -exp - 1) + digits).TrimEnd('0');
            body = "0." + frac;
        }
        return neg ? "-" + body : body;
    }

    static bool NeedsQuotes(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r'
        or '"' or '=' or '!' or '<' or '>' or '(' or ')' or '{' or '}' or '[' or ']' or '@' or ',' or '#' or ':';

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A name or value: bare unless it is empty or has a character that would end it.</summary>
    public static string Quoted(string s)
    {
        if (s.Length == 0) return "\"\"";
        foreach (char c in s)
            if (NeedsQuotes(c)) return "\"" + Escape(s) + "\"";
        return s;
    }
}

sealed class XLinkTextWriter
{
    enum SwitchKind { Null, Local, Global, ActionSlot }

    readonly XLinkFile f;
    readonly StringBuilder sb = new();
    int indent;

    XLinkTextWriter(XLinkFile file) => f = file;

    public static string Write(XLinkFile file)
    {
        var w = new XLinkTextWriter(file);
        w.WriteFile();
        return w.sb.ToString();
    }

    void Indents() { for (int i = 0; i < indent; i++) sb.Append("  "); }

    void Line(string text) { Indents(); sb.Append(text).Append('\n'); }

    void Open(string header, bool indented = true)
    {
        if (indented) Indents();
        sb.Append(header.Length == 0 ? "{\n" : header + " {\n");
        indent++;
    }

    void Close()
    {
        indent--;
        Line("}");
    }

    static string Scope(bool global, string name) => (global ? "Global::" : "Local::") + TextFormat.Quoted(name);

    void WriteFile()
    {
        Open("Metadata");
        Line("ModuleType = " + f.Module);
        Close();

        int systemUser = f.Module == ModuleType.SLink ? 8 : 0;
        int systemAsset = f.AssetParams.Count - f.NumCustomAssetParam;
        Open("ParamDefines");
        DefineSet("SystemUserParams", f.UserParams.Take(systemUser));
        DefineSet("CustomUserParams", f.UserParams.Skip(systemUser));
        DefineSet("SystemAssetParams", f.AssetParams.Take(systemAsset));
        DefineSet("CustomAssetParams", f.AssetParams.Skip(systemAsset));
        DefineSet("TriggerParams", f.TriggerParams);
        Close();

        Open("Users");
        foreach (var u in f.Users) WriteUser(u);
        Close();
    }

    void DefineSet(string name, IEnumerable<ParamDefine> defines)
    {
        Open(name);
        foreach (var d in defines)
        {
            string value = d.Type switch
            {
                ParamType.Int => Convert.ToInt32(d.Default, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                ParamType.Float => TextFormat.Float(Convert.ToSingle(d.Default, CultureInfo.InvariantCulture)),
                ParamType.Bool => TextFormat.Bool(d.Default is true),
                ParamType.Enum => TextFormat.Hex(Convert.ToUInt32(d.Default, CultureInfo.InvariantCulture)),
                ParamType.String => "\"" + TextFormat.Escape((string?)d.Default ?? "") + "\"",
                _ => "<custom>",
            };
            Line(d.Name + " = " + value);
        }
        Close();
    }

    void WriteParams(IEnumerable<KeyValuePair<string, ParamValue>> ps)
    {
        foreach (var (name, v) in ps) WriteParam(name, v);
    }

    void WriteParam(string name, ParamValue v)
    {
        Indents();
        sb.Append(name).Append(" = ");
        switch (v.RefType)
        {
            case RefType.Direct:
                sb.Append(v.Value switch
                {
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    float x => TextFormat.Float(x),
                    bool b => TextFormat.Bool(b),
                    uint u => TextFormat.Hex(u),
                    _ => throw new InvalidDataException($"Unsupported value for {name}."),
                }).Append('\n');
                break;
            case RefType.Bitflag:
                sb.Append(TextFormat.Bin((uint)v.Value!)).Append('\n');
                break;
            case RefType.String:
                sb.Append('"').Append(TextFormat.Escape((string)v.Value!)).Append("\"\n");
                break;
            case RefType.Curve:
            {
                var c = (Curve)v.Value!;
                Open("CURVE", false);
                Line("Type = " + c.Type switch { 0 => "Standard", 1 => "Constant", _ => throw new InvalidDataException($"Unknown curve type {c.Type}.") });
                Line("Property = " + Scope(c.IsGlobal, c.PropertyName));
                Line("Unknown = " + c.Unknown.ToString(CultureInfo.InvariantCulture));
                Line("UpdateType = " + c.UpdateType switch { 0 => "Update", 1 => "NoUpdate", _ => throw new InvalidDataException($"Unknown curve update type {c.UpdateType}.") });
                Open("Points =");
                foreach (var p in c.Points) Line($"({TextFormat.Float(p.X)}, {TextFormat.Float(p.Y)})");
                Close();
                Close();
                break;
            }
            case RefType.ArrangeGroup:
            {
                Open("ARRANGE", false);
                foreach (var g in (List<ArrangeGroup>)v.Value!)
                {
                    Open(TextFormat.Quoted(g.Name));
                    Line("LimitType = " + g.LimitType);
                    Line("Threshold = " + g.Threshold.ToString(CultureInfo.InvariantCulture));
                    Line("IncludeFading = " + TextFormat.Bool(g.IncludeFading));
                    Close();
                }
                Close();
                break;
            }
            default:
            {
                var r = (RandomRange)v.Value!;
                (string type, float power) = v.RefType switch
                {
                    RefType.Random => ("Linear", 0f),
                    RefType.Random2Pow => ("InflectedPolynomial", 2f),
                    RefType.Random3Pow => ("InflectedPolynomial", 3f),
                    RefType.Random4Pow => ("InflectedPolynomial", 4f),
                    RefType.Random1Point5Pow => ("InflectedPolynomial", 1.5f),
                    RefType.Random2PowWeightMin => ("IncreasingPolynomial", 2f),
                    RefType.Random3PowWeightMin => ("IncreasingPolynomial", 3f),
                    RefType.Random4PowWeightMin => ("IncreasingPolynomial", 4f),
                    RefType.Random1Point5PowWeightMin => ("IncreasingPolynomial", 1.5f),
                    RefType.Random2PowWeightMax => ("DecreasingPolynomial", 2f),
                    RefType.Random3PowWeightMax => ("DecreasingPolynomial", 3f),
                    RefType.Random4PowWeightMax => ("DecreasingPolynomial", 4f),
                    _ => ("DecreasingPolynomial", 1.5f),
                };
                Open("RANDOM", false);
                Line("Type = " + type);
                if (type != "Linear") Line("Power = " + TextFormat.Float(power));
                Line("Min = " + TextFormat.Float(r.Min));
                Line("Max = " + TextFormat.Float(r.Max));
                Close();
                break;
            }
        }
    }

    // ---- users ----

    sealed class Tree
    {
        public required User User;
        public required List<int>[] Kids;
        public required bool[] HasParent;
        public readonly HashSet<int> Written = [];
    }

    Tree Resolve(User u)
    {
        int n = u.AssetCalls.Count;
        var tree = new Tree { User = u, Kids = new List<int>[n], HasParent = new bool[n] };
        for (int i = 0; i < n; i++) tree.Kids[i] = [];
        var seen = new HashSet<int>();

        void Visit(int i, HashSet<int> active)
        {
            if (active.Contains(i)) throw new InvalidDataException($"Circular reference detected at {u.AssetCalls[i].KeyName}.");
            if (!seen.Add(i)) return;
            active.Add(i);
            var c = u.AssetCalls[i].Container;
            if (c is not null)
            {
                if (c.Type == ContainerType.Jump)
                {
                    if (c.ChildrenStart >= 0)
                    {
                        tree.Kids[i].Add(c.ChildrenStart);
                        Visit(c.ChildrenStart, active);
                    }
                }
                else
                {
                    if (c.ChildrenStart < 0) throw new InvalidDataException($"Negative child index in {u.AssetCalls[i].KeyName}.");
                    for (int k = c.ChildrenStart; k <= c.ChildrenEnd; k++)
                    {
                        if (k >= n) throw new InvalidDataException("Child call table index out of range.");
                        tree.HasParent[k] = true;
                        tree.Kids[i].Add(k);
                        Visit(k, active);
                    }
                }
            }
            active.Remove(i);
        }

        for (int i = 0; i < n; i++) Visit(i, []);
        return tree;
    }

    void WriteUser(User u)
    {
        var tree = Resolve(u);
        Open(u.Name is not null ? TextFormat.Quoted(u.Name) : TextFormat.Hex8(u.NameHash));
        Line("Unknown = " + u.Unknown.ToString(CultureInfo.InvariantCulture));

        Open("UserParams");
        WriteParams(u.Params);
        Close();

        var locals = u.LocalProperties.Distinct().ToList();
        locals.Sort(string.CompareOrdinal);
        if (locals.Count > 0)
        {
            // std::set<std::string> orders by byte, so compare the UTF-8 bytes.
            locals.Sort((a, b) => XLinkFile.Utf8.GetBytes(a).AsSpan().SequenceCompareTo(XLinkFile.Utf8.GetBytes(b)));
            Open("LocalProperties");
            foreach (var l in locals) Line(TextFormat.Quoted(l));
            Close();
        }

        if (u.ActionSlots.Count > 0)
        {
            Open("ActionSlots");
            foreach (var s in u.ActionSlots) WriteActionSlot(u, s);
            Close();
        }

        if (u.Properties.Count > 0)
        {
            Open("Properties");
            foreach (var p in u.Properties) WriteProperty(u, p);
            Close();
        }

        if (u.AlwaysTriggers.Count > 0)
        {
            Open("AlwaysTriggers");
            foreach (var t in u.AlwaysTriggers)
            {
                Open(TextFormat.Hex8(t.Guid));
                Line("Flags = " + t.Flag.ToString(CultureInfo.InvariantCulture));
                Line("Unknown = " + t.OverwriteHash.ToString(CultureInfo.InvariantCulture));
                Line("Asset = " + Ref(u, t.AssetCallIndex));
                WriteOverwrite(t);
                Close();
            }
            Close();
        }

        Open("AssetCallTables");
        for (int i = 0; i < u.AssetCalls.Count; i++)
            if (!tree.HasParent[i]) WriteCall(tree, i, true);
        Close();
        Close();
    }

    static string Ref(User u, int index)
    {
        if (index < 0 || index >= u.AssetCalls.Count) throw new InvalidDataException($"Asset call table index {index} out of range.");
        var a = u.AssetCalls[index];
        return TextFormat.Quoted(a.KeyName) + "[" + TextFormat.Hex8(a.Guid) + "]";
    }

    void WriteOverwrite(Trigger t)
    {
        if (t.Overwrite is not { Count: > 0 }) return;
        Open("OverwriteParams = ");
        WriteParams(t.Overwrite);
        Close();
    }

    static TriggerKind KindOf(Trigger t) =>
        (t.Flag & 4) != 0 ? TriggerKind.Always : (t.Flag & 8) != 0 ? TriggerKind.OnLeave : (t.Flag & 0x10) != 0 ? TriggerKind.Previous : TriggerKind.FrameWindow;

    enum TriggerKind { FrameWindow, Always, OnLeave, Previous }

    void WriteActionSlot(User u, ActionSlot slot)
    {
        Open(TextFormat.Quoted(slot.Name));
        foreach (var action in slot.Actions)
        {
            if (action.IsPrefix) Line("@Prefix");
            Open(TextFormat.Quoted(action.Name));
            foreach (var t in action.Triggers)
            {
                Open(TextFormat.Hex8(t.Guid));
                var kind = KindOf(t);
                Line("Type = " + kind);
                Line("Start = " + (kind == TriggerKind.FrameWindow ? t.StartFrame : 0).ToString(CultureInfo.InvariantCulture));
                Line("End = " + t.EndFrame.ToString(CultureInfo.InvariantCulture));
                if (kind == TriggerKind.Previous) Line("PreviousAction = " + TextFormat.Quoted(t.PreviousActionName ?? ""));
                Line("Unknown1 = " + ((uint)t.Unknown1).ToString(CultureInfo.InvariantCulture));
                Line("Unknown2 = " + t.OverwriteHash.ToString(CultureInfo.InvariantCulture));
                Line("Oneshot = " + TextFormat.Bool((t.Flag & 1) != 0));
                Line("Asset = " + Ref(u, t.AssetCallIndex));
                WriteOverwrite(t);
                Close();
            }
            Close();
        }
        Close();
    }

    void WriteProperty(User u, Property p)
    {
        Open(Scope(p.IsGlobal, p.WatchPropertyName));
        var kind = p.IsGlobal ? SwitchKind.Global : SwitchKind.Local;
        foreach (var t in p.Triggers)
        {
            Open($"if {Cond(t.Condition, kind, p.WatchPropertyName)} => {TextFormat.Hex8(t.Guid)}");
            Line("Lazy = " + TextFormat.Bool((t.Flag & 1) != 0));
            Line("Unknown = " + t.OverwriteHash.ToString(CultureInfo.InvariantCulture));
            Line("Asset = " + Ref(u, t.AssetCallIndex));
            WriteOverwrite(t);
            Close();
        }
        Close();
    }

    // ---- conditions ----

    static string Compare(CompareType c) => c switch
    {
        CompareType.Equal => "==",
        CompareType.GreaterThan => ">",
        CompareType.GreaterThanOrEqual => ">=",
        CompareType.LessThan => "<",
        CompareType.LessThanOrEqual => "<=",
        _ => "!=",
    };

    static string Cond(SwitchCondition? c, SwitchKind kind, string varName)
    {
        if (c is null) return "_";
        string cmp = Compare(c.Compare);
        return c.Value switch
        {
            string s when kind == SwitchKind.ActionSlot => $"<value> {cmp} <action>::{TextFormat.Quoted(s)}",
            string s => $"<value> {cmp} {TextFormat.Quoted(varName)}::{TextFormat.Quoted(s)}",
            int i => $"<value> {cmp} {i.ToString(CultureInfo.InvariantCulture)}",
            float x => $"<value> {cmp} {TextFormat.Float(x)}",
            bool b => $"<value> {cmp} {TextFormat.Bool(b)}",
            _ => throw new InvalidDataException("Unsupported switch condition value."),
        };
    }

    // ---- asset call tables ----

    void WriteCall(Tree tree, int index, bool indented)
    {
        var u = tree.User;
        var a = u.AssetCalls[index];
        string reference = Ref(u, index);
        if (!tree.Written.Add(index))
        {
            if (indented) Line(reference);
            else sb.Append(reference).Append('\n');
            return;
        }

        Open(reference, indented);
        Line("EmitCount = " + a.Duration.ToString(CultureInfo.InvariantCulture));
        Line("Oneshot = " + TextFormat.Bool((a.Flag & 8) != 0));
        Line("NoPause = " + TextFormat.Bool((a.Flag & 0x10) != 0));
        Line("UserFlags = " + TextFormat.Bin((uint)((a.Flag >> 8) & 0xFF)));

        var c = a.Container;
        if (c is null)
        {
            Open("Execute = Asset");
            WriteParams(a.Params ?? []);
            Close();
            Close();
            return;
        }

        var kids = tree.Kids[index];
        switch (c.Type)
        {
            case ContainerType.Switch:
            {
                var kind = c.IsAction ? SwitchKind.ActionSlot : c.IsGlobal ? SwitchKind.Global : c.WatchPropertyIndex >= 0 ? SwitchKind.Local : SwitchKind.Null;
                string name = c.WatchPropertyName ?? "";
                Line("@Unknown = " + c.WatchPropertyId.ToString(CultureInfo.InvariantCulture));
                string target = kind switch
                {
                    SwitchKind.ActionSlot => "ActionSlot::" + TextFormat.Quoted(name),
                    SwitchKind.Local => "Local::" + TextFormat.Quoted(name),
                    SwitchKind.Global => "Global::" + TextFormat.Quoted(name),
                    _ => "<null>",
                };
                Open($"Execute = Switch ({target})");
                foreach (var k in kids)
                {
                    Indents();
                    sb.Append('(').Append(Cond(u.AssetCalls[k].Condition as SwitchCondition, kind, name)).Append(") => ");
                    WriteCall(tree, k, false);
                }
                Close();
                break;
            }
            case ContainerType.Random or ContainerType.Random2:
            {
                Open(c.Type == ContainerType.Random ? "Execute = Random" : "Execute = RandomNoRepeat");
                foreach (var k in kids)
                {
                    if (u.AssetCalls[k].Condition is not RandomCondition rc) throw new InvalidDataException($"Random container child {u.AssetCalls[k].KeyName} has no weight.");
                    Indents();
                    sb.Append(TextFormat.Float(rc.Weight)).Append(" => ");
                    WriteCall(tree, k, false);
                }
                Close();
                break;
            }
            case ContainerType.Blend when c.IsBlendBy:
            {
                Line("@Unknown = " + c.WatchPropertyId.ToString(CultureInfo.InvariantCulture));
                Open($"Execute = BlendBy ({Scope(c.IsGlobal, c.WatchPropertyName ?? "")})");
                foreach (var k in kids)
                {
                    if (u.AssetCalls[k].Condition is not BlendCondition bc) throw new InvalidDataException($"BlendBy container child {u.AssetCalls[k].KeyName} has no range.");
                    Indents();
                    sb.Append($"[Min: {TextFormat.Float(bc.Min)}, Op: {bc.MinOp}], [Max: {TextFormat.Float(bc.Max)}, Op: {bc.MaxOp}]".Insert(0, "(")).Append(") => ");
                    WriteCall(tree, k, false);
                }
                Close();
                break;
            }
            case ContainerType.Blend:
            {
                Open("Execute = Blend");
                foreach (var k in kids) WriteCall(tree, k, true);
                Close();
                break;
            }
            case ContainerType.Sequence:
            {
                Open("Execute = Sequence");
                foreach (var k in kids)
                {
                    if (u.AssetCalls[k].Condition is SequenceCondition sc) Line("@ForceContinue = " + ((uint)sc.ForceContinue).ToString(CultureInfo.InvariantCulture));
                    WriteCall(tree, k, true);
                }
                Close();
                break;
            }
            case ContainerType.Grid:
            {
                if (c.Cells.Count != c.Values1.Count * c.Values2.Count) throw new InvalidDataException("Grid must contain a case for each combination of values.");
                string n1 = TextFormat.Quoted(c.Property1Name ?? ""), n2 = TextFormat.Quoted(c.Property2Name ?? "");
                Open($"Execute = Grid ({Scope(c.IsProperty1Global, c.Property1Name ?? "")}, {Scope(c.IsProperty2Global, c.Property2Name ?? "")})");
                Open("Cases");
                for (int i = 0; i < c.Values1.Count; i++)
                    for (int j = 0; j < c.Values2.Count; j++)
                    {
                        int cell = c.Cells[i * c.Values2.Count + j];
                        string target = cell < 0 ? "<null>" : Ref(u, cell);
                        Line($"({n1}::{TextFormat.Quoted(c.Values1[i])}, {n2}::{TextFormat.Quoted(c.Values2[j])}) => {target}");
                    }
                Close();
                Open("Children");
                foreach (var k in kids) WriteCall(tree, k, true);
                Close();
                Close();
                break;
            }
            case ContainerType.Jump:
                Line("Execute = Jump => " + (kids.Count > 0 ? Ref(u, kids[0]) : "<null>"));
                break;
        }
        Close();
    }
}
