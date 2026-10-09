using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XLinkSharp;

enum Tk
{
    Word, Dec, Hex, Bin, Float, Str,
    Assign, Arrow, Eq, Ne, Lt, Gt, Le, Ge,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    At, Comma, Colon, Scope, End,
}

readonly record struct Token(Tk Kind, string Text, int Line)
{
    public bool HasValue => Kind is Tk.Word or Tk.Dec or Tk.Hex or Tk.Bin or Tk.Float or Tk.Str;
}

/// <summary>Splits the xlink2 text form into tokens; words run until a character that can end one.</summary>
static partial class TextLexer
{
    [GeneratedRegex(@"^[+-]?[0-9]+$")] private static partial Regex DecRx();
    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")] private static partial Regex HexRx();
    [GeneratedRegex(@"^0b[01]+$")] private static partial Regex BinRx();
    [GeneratedRegex(@"^[+-]?([0-9]+\.[0-9]*|[0-9]+)([eE][+-]?[0-9]+)?$")] private static partial Regex FloatRx();
    [GeneratedRegex(@"^[+-]?(inf|nan)$", RegexOptions.IgnoreCase)] private static partial Regex SpecialRx();

    static bool IsSpace(char c) => c is ' ' or (>= '\t' and <= '\r');

    static bool EndsWord(char c) => IsSpace(c) || c is '"' or '=' or '!' or '<' or '>' or '(' or ')' or '{' or '}' or '[' or ']' or '@' or ',' or '#' or ':';

    static Tk Classify(string w)
    {
        if (DecRx().IsMatch(w)) return Tk.Dec;
        if (HexRx().IsMatch(w)) return Tk.Hex;
        if (BinRx().IsMatch(w)) return Tk.Bin;
        if (SpecialRx().IsMatch(w)) return Tk.Float;
        if (FloatRx().IsMatch(w) && (w.Contains('.') || w.Contains('e') || w.Contains('E'))) return Tk.Float;
        return Tk.Word;
    }

    public static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1;
        void Add(Tk kind, string text = "") => tokens.Add(new Token(kind, text, line));
        bool Next(char c) => i + 1 < s.Length && s[i + 1] == c;

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (IsSpace(c)) { i++; continue; }
            switch (c)
            {
                case '#':
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                case '"':
                {
                    int start = ++i, slashes = 0;
                    while (true)
                    {
                        if (i >= s.Length || s[i] == '\n') throw new FormatException($"Line {line}: unclosed string.");
                        if (s[i] == '\\') slashes++;
                        else if (s[i] == '"' && slashes % 2 == 0) break;
                        else slashes = 0;
                        i++;
                    }
                    Add(Tk.Str, s[start..i]);
                    i++;
                    continue;
                }
                case '=':
                    if (Next('=')) { Add(Tk.Eq); i += 2; }
                    else if (Next('>')) { Add(Tk.Arrow); i += 2; }
                    else { Add(Tk.Assign); i++; }
                    continue;
                case '!':
                    if (!Next('=')) throw new FormatException($"Line {line}: unexpected '!'.");
                    Add(Tk.Ne); i += 2;
                    continue;
                case '<':
                    if (Next('=')) { Add(Tk.Le); i += 2; } else { Add(Tk.Lt); i++; }
                    continue;
                case '>':
                    if (Next('=')) { Add(Tk.Ge); i += 2; } else { Add(Tk.Gt); i++; }
                    continue;
                case '(': Add(Tk.LParen); i++; continue;
                case ')': Add(Tk.RParen); i++; continue;
                case '{': Add(Tk.LBrace); i++; continue;
                case '}': Add(Tk.RBrace); i++; continue;
                case '[': Add(Tk.LBracket); i++; continue;
                case ']': Add(Tk.RBracket); i++; continue;
                case '@': Add(Tk.At); i++; continue;
                case ',': Add(Tk.Comma); i++; continue;
                case ':':
                    if (Next(':')) { Add(Tk.Scope); i += 2; } else { Add(Tk.Colon); i++; }
                    continue;
            }
            int from = i;
            while (i < s.Length && !EndsWord(s[i])) i++;
            string word = s[from..i];
            Add(Classify(word), word);
        }
        Add(Tk.End);
        return tokens;
    }
}

sealed class XLinkTextParser
{
    // ---- parsed shapes, before they are laid out as the binary's flat tables ----

    abstract record PValue;
    sealed record PInt(int Value) : PValue;
    sealed record PBits(uint Value) : PValue;
    sealed record PEnum(uint Value) : PValue;
    sealed record PFloat(float Value) : PValue;
    sealed record PBool(bool Value) : PValue;
    sealed record PString(string Value) : PValue;
    sealed record PCurve(Curve Value) : PValue;
    sealed record PRandom(RefType Ref, RandomRange Value) : PValue;
    sealed record PArrange(List<ArrangeGroup> Value) : PValue;

    enum SwitchKind { Null, Local, Global, ActionSlot }

    sealed class PCond
    {
        public CompareType Compare;
        public bool IsDefault;
        public object Value = 0;
    }

    sealed class PCall
    {
        public string Key = "";
        public uint Guid;
        public int EmitCount = 1;
        public bool OneShot, NoPause;
        public byte UserFlags;
        public string? Kind;                                   // null until Execute is read
        public SwitchKind Switch;
        public string Variable = "";
        public int Unknown = -1;
        public string Prop1 = "", Prop2 = "";
        public bool Global1, Global2;
        public List<string> Values1 = [], Values2 = [];
        public List<(string V1, string V2, uint Guid)> Cases = [];
        public List<PCall> Children = [];
        public uint? JumpGuid;
        public PCall? JumpInline;
        public List<KeyValuePair<string, PValue>> Params = [];
        // set by the parent
        public PCond? Cond;
        public float Weight;
        public float Min, Max;
        public BlendOp MinOp, MaxOp;
        public bool HasForceContinue;
        public uint ForceContinue;
    }

    sealed class PTrigger
    {
        public uint Guid;
        public uint Asset;
        public bool HasAsset;
        public string Type = "FrameWindow";
        public int Start, End = int.MaxValue;
        public string Previous = "";
        public uint Unknown1;
        public uint Unknown2;
        public bool OneShot, Lazy;
        public ushort Flags;
        public PCond? Cond;
        public List<KeyValuePair<string, PValue>> Overwrite = [];
    }

    sealed class PAction { public string Name = ""; public bool Prefix; public List<PTrigger> Triggers = []; }
    sealed class PSlot { public string Name = ""; public List<PAction> Actions = []; }
    sealed class PProperty { public bool Global; public string Name = ""; public List<PTrigger> Triggers = []; }

    sealed class PUser
    {
        public uint Hash;
        public string? Name;
        public short Unknown = -1;
        public List<KeyValuePair<string, PValue>> Params = [];
        public List<string> Locals = [];
        public List<PSlot> Slots = [];
        public List<PProperty> Properties = [];
        public List<PTrigger> Always = [];
        public List<PCall> Roots = [];
        public Dictionary<uint, PCall> ByGuid = [];
    }

    // ---- token access ----

    readonly List<Token> t;
    int p;
    readonly XLinkFile f;
    readonly Dictionary<string, ParamDefine> userDefs = [], assetDefs = [], triggerDefs = [];

    XLinkTextParser(string text, XLinkGame game)
    {
        t = TextLexer.Lex(text);
        f = new XLinkFile { Game = game };
    }

    public static XLinkFile Parse(string text, XLinkGame game) => new XLinkTextParser(text, game).ParseFile();

    Token Peek => t[p];
    Token Take() => t[p++];

    FormatException Error(string message, Token? at = null) => new($"Line {(at ?? Peek).Line}: {message}");

    Token Expect(Tk kind)
    {
        var tok = Take();
        if (tok.Kind != kind) throw Error($"expected {kind} but found {Describe(tok)}.", tok);
        return tok;
    }

    static string Describe(Token tok) => tok.Kind == Tk.End ? "the end of the file" : tok.Text.Length > 0 ? $"'{tok.Text}'" : tok.Kind.ToString();

    bool TryTake(Tk kind)
    {
        if (Peek.Kind != kind) return false;
        p++;
        return true;
    }

    string TakeName()
    {
        var tok = Take();
        if (!tok.HasValue) throw Error($"expected a name but found {Describe(tok)}.", tok);
        return tok.Kind == Tk.Str ? Unescape(tok.Text) : tok.Text;
    }

    string TakeWord()
    {
        var tok = Take();
        if (!tok.HasValue) throw Error($"expected an identifier but found {Describe(tok)}.", tok);
        return tok.Text;
    }

    void ExpectWord(string word)
    {
        var tok = Take();
        if (tok.Kind != Tk.Word || tok.Text != word) throw Error($"expected '{word}' but found {Describe(tok)}.", tok);
    }

    static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            switch (s[i + 1])
            {
                case '"': sb.Append('"'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                case 'b': sb.Append('\b'); i++; break;
                case 'f': sb.Append('\f'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'u' when i + 5 < s.Length && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v):
                    sb.Append((char)v); i += 5; break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    int TakeInt()
    {
        var tok = Take();
        try
        {
            return tok.Kind switch
            {
                Tk.Dec => int.Parse(tok.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                Tk.Hex => unchecked((int)uint.Parse(tok.Text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)),
                Tk.Bin => unchecked((int)Convert.ToUInt32(tok.Text[2..], 2)),
                _ => throw Error($"expected an integer but found {Describe(tok)}.", tok),
            };
        }
        catch (OverflowException) { throw Error($"integer {tok.Text} is out of range.", tok); }
    }

    uint TakeUInt()
    {
        var tok = Take();
        try
        {
            return tok.Kind switch
            {
                Tk.Dec => uint.Parse(tok.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                Tk.Hex => uint.Parse(tok.Text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
                Tk.Bin => Convert.ToUInt32(tok.Text[2..], 2),
                _ => throw Error($"expected an integer but found {Describe(tok)}.", tok),
            };
        }
        catch (OverflowException) { throw Error($"integer {tok.Text} is out of range.", tok); }
    }

    static float ParseFloat(string text)
    {
        string unsigned = text.TrimStart('+', '-');
        bool neg = text.StartsWith('-');
        if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase)) return neg ? float.NegativeInfinity : float.PositiveInfinity;
        if (unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase)) return neg ? -float.NaN : float.NaN;
        return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    float TakeFloat()
    {
        var tok = Take();
        return tok.Kind switch
        {
            Tk.Float => ParseFloat(tok.Text),
            Tk.Dec => int.Parse(tok.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
            _ => throw Error($"expected a number but found {Describe(tok)}.", tok),
        };
    }

    bool TakeBool() => TakeWord() == "true";

    TEnum TakeEnum<TEnum>() where TEnum : struct, Enum
    {
        var tok = Take();
        if (!tok.HasValue || !Enum.TryParse(tok.Text, out TEnum value) || !Enum.IsDefined(value))
            throw Error($"'{tok.Text}' is not a {typeof(TEnum).Name}.", tok);
        return value;
    }

    // ---- file ----

    XLinkFile ParseFile()
    {
        bool metadata = false, defines = false, users = false;
        List<PUser> parsedUsers = [];
        while (Peek.Kind != Tk.End)
        {
            var tok = Take();
            if (tok.Kind != Tk.Word) throw Error($"expected Metadata, ParamDefines or Users but found {Describe(tok)}.", tok);
            switch (tok.Text)
            {
                case "Metadata": ParseMetadata(); metadata = true; break;
                case "ParamDefines": ParseDefines(); defines = true; break;
                case "Users": parsedUsers = ParseUsers(); users = true; break;
                default: throw Error($"expected Metadata, ParamDefines or Users but found '{tok.Text}'.", tok);
            }
        }
        if (!metadata || !defines || !users) throw new FormatException("The text needs Metadata, ParamDefines and Users sections.");

        foreach (var u in parsedUsers) f.Users.Add(Build(u));
        return f;
    }

    void ParseMetadata()
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var key = Take();
            if (key.Kind != Tk.Word || key.Text != "ModuleType") throw Error("unknown metadata key.", key);
            Expect(Tk.Assign);
            f.Module = TakeEnum<ModuleType>();
        }
    }

    void ParseDefines()
    {
        Expect(Tk.LBrace);
        int customAsset = 0;
        while (!TryTake(Tk.RBrace))
        {
            var cat = Take();
            List<ParamDefine> target;
            switch (cat.Text)
            {
                case "SystemUserParams" or "CustomUserParams": target = f.UserParams; break;
                case "SystemAssetParams" or "CustomAssetParams": target = f.AssetParams; break;
                case "TriggerParams": target = f.TriggerParams; break;
                default: throw Error($"unknown ParamDefine category '{cat.Text}'.", cat);
            }
            int before = target.Count;
            Expect(Tk.LBrace);
            while (!TryTake(Tk.RBrace)) target.Add(ParseDefine());
            if (cat.Text == "CustomAssetParams") customAsset += target.Count - before;
        }
        f.NumCustomAssetParam = customAsset;
        foreach (var d in f.UserParams) userDefs[d.Name] = d;
        foreach (var d in f.AssetParams) assetDefs[d.Name] = d;
        foreach (var d in f.TriggerParams) triggerDefs[d.Name] = d;
    }

    ParamDefine ParseDefine()
    {
        string name = TakeName();
        Expect(Tk.Assign);
        var v = Take();
        switch (v.Kind)
        {
            case Tk.Word when v.Text == "true": return new(name, ParamType.Bool, true);
            case Tk.Word when v.Text == "false": return new(name, ParamType.Bool, false);
            case Tk.Word: return new(name, ParamType.String, v.Text);
            case Tk.Str: return new(name, ParamType.String, Unescape(v.Text));
            case Tk.Dec or Tk.Bin: p--; return new(name, ParamType.Int, TakeInt());
            case Tk.Hex: p--; return new(name, ParamType.Enum, TakeUInt());
            case Tk.Float: return new(name, ParamType.Float, ParseFloat(v.Text));
            case Tk.Lt:
                ExpectWord("custom");
                Expect(Tk.Gt);
                return new(name, ParamType.Custom, null);
            default: throw Error($"cannot read the value of {name}.", v);
        }
    }

    // ---- params ----

    List<KeyValuePair<string, PValue>> ParseParams()
    {
        var list = new List<KeyValuePair<string, PValue>>();
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            string key = TakeName();
            Expect(Tk.Assign);
            var v = Take();
            PValue value;
            switch (v.Kind)
            {
                case Tk.Word when v.Text == "true": value = new PBool(true); break;
                case Tk.Word when v.Text == "false": value = new PBool(false); break;
                case Tk.Word when v.Text == "RANDOM": value = ParseRandom(); break;
                case Tk.Word when v.Text == "CURVE": value = ParseCurve(); break;
                case Tk.Word when v.Text == "ARRANGE": value = ParseArrange(); break;
                case Tk.Word: value = new PString(v.Text); break;
                case Tk.Str: value = new PString(Unescape(v.Text)); break;
                case Tk.Bin: p--; value = new PBits(TakeUInt()); break;
                case Tk.Dec: p--; value = new PInt(TakeInt()); break;
                case Tk.Hex: p--; value = new PEnum(TakeUInt()); break;
                case Tk.Float: value = new PFloat(ParseFloat(v.Text)); break;
                default: throw Error($"cannot read the value of {key}.", v);
            }
            list.Add(new(key, value));
        }
        return list;
    }

    PValue ParseRandom()
    {
        string type = "Linear";
        float power = 2f, min = 0f, max = 1f;
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var key = Take();
            Expect(Tk.Assign);
            switch (key.Text)
            {
                case "Type": type = TakeWord(); break;
                case "Power": power = TakeFloat(); break;
                case "Min": min = TakeFloat(); break;
                case "Max": max = TakeFloat(); break;
                default: throw Error($"unknown random attribute '{key.Text}'.", key);
            }
        }
        int slot = power switch { 2f => 0, 3f => 1, 4f => 2, 1.5f => 3, _ => -1 };
        if (type != "Linear" && slot < 0) slot = 0; // the tool ignores a power it does not support
        RefType rt = type switch
        {
            "Linear" => RefType.Random,
            "InflectedPolynomial" => (RefType)((byte)RefType.Random2Pow + slot),
            "IncreasingPolynomial" => (RefType)((byte)RefType.Random2PowWeightMin + slot),
            "DecreasingPolynomial" => (RefType)((byte)RefType.Random2PowWeightMax + slot),
            _ => throw new FormatException($"'{type}' is not a RandomType."),
        };
        return new PRandom(rt, new RandomRange(min, max));
    }

    PValue ParseCurve()
    {
        var c = new Curve();
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var key = Take();
            Expect(Tk.Assign);
            switch (key.Text)
            {
                case "Type":
                    c.Type = (ushort)(TakeWord() switch { "Standard" => 0, "Constant" => 1, var o => throw Error($"'{o}' is not a CurveType.") });
                    break;
                case "Property":
                    string scope = TakeWord();
                    if (scope is not ("Local" or "Global")) throw Error($"'{scope}' is not a PropertyScope.");
                    c.IsGlobal = scope == "Global";
                    Expect(Tk.Scope);
                    c.PropertyName = TakeName();
                    break;
                case "Unknown": c.Unknown = TakeInt(); break;
                case "UpdateType":
                    c.UpdateType = (short)(TakeWord() switch { "Update" => 0, "NoUpdate" => 1, var o => throw Error($"'{o}' is not a CurveUpdateMode.") });
                    break;
                case "Points":
                    Expect(Tk.LBrace);
                    while (!TryTake(Tk.RBrace))
                    {
                        Expect(Tk.LParen);
                        float x = TakeFloat();
                        Expect(Tk.Comma);
                        float y = TakeFloat();
                        Expect(Tk.RParen);
                        c.Points.Add(new(x, y));
                    }
                    break;
                default: throw Error($"unknown curve attribute '{key.Text}'.", key);
            }
        }
        return new PCurve(c);
    }

    PValue ParseArrange()
    {
        var groups = new List<ArrangeGroup>();
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            string name = TakeName();
            var limit = LimitType.PriorityThenOldest;
            sbyte threshold = -1;
            bool fading = false;
            Expect(Tk.LBrace);
            while (!TryTake(Tk.RBrace))
            {
                var key = Take();
                Expect(Tk.Assign);
                switch (key.Text)
                {
                    case "LimitType": limit = TakeEnum<LimitType>(); break;
                    case "Threshold": threshold = unchecked((sbyte)TakeInt()); break;
                    case "IncludeFading": fading = TakeBool(); break;
                    default: throw Error($"unknown arrange group attribute '{key.Text}'.", key);
                }
            }
            groups.Add(new(name, limit, threshold, fading));
        }
        return new PArrange(groups);
    }

    // ---- users ----

    List<PUser> ParseUsers()
    {
        var users = new List<PUser>();
        var seen = new HashSet<uint>();
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var id = Take();
            var u = new PUser();
            if (!id.HasValue || id.Kind == Tk.Float) throw Error("expected a user name or hash.", id);
            if (id.Kind == Tk.Hex) u.Hash = uint.Parse(id.Text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            else
            {
                u.Name = id.Kind == Tk.Str ? Unescape(id.Text) : id.Text;
                u.Hash = XLinkFile.Crc32(u.Name);
            }
            if (!seen.Add(u.Hash)) throw Error($"user hash 0x{u.Hash:x8} is used twice.", id);
            ParseUser(u);
            users.Add(u);
        }
        return users;
    }

    void ParseUser(PUser u)
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var key = Take();
            switch (key.Text)
            {
                case "Unknown": Expect(Tk.Assign); u.Unknown = (short)TakeInt(); break;
                case "UserParams": u.Params = ParseParams(); break;
                case "LocalProperties":
                    Expect(Tk.LBrace);
                    while (!TryTake(Tk.RBrace)) u.Locals.Add(TakeName());
                    break;
                case "ActionSlots": ParseSlots(u); break;
                case "Properties": ParseProperties(u); break;
                case "AlwaysTriggers": ParseAlways(u); break;
                case "AssetCallTables":
                    Expect(Tk.LBrace);
                    while (!TryTake(Tk.RBrace))
                    {
                        var (name, guid) = TakeCallKey();
                        u.Roots.Add(ParseCall(u, name, guid) ?? throw Error($"{name}[0x{guid:x8}] is declared without a body."));
                    }
                    break;
                default: throw Error($"unknown user section '{key.Text}'.", key);
            }
        }
    }

    (string Name, uint Guid) TakeCallKey()
    {
        string name = TakeName();
        Expect(Tk.LBracket);
        uint guid = TakeUInt();
        Expect(Tk.RBracket);
        return (name, guid);
    }

    // ---- triggers ----

    void ParseTriggerBody(PTrigger tr, bool action)
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var key = Take();
            Expect(Tk.Assign);
            switch (key.Text)
            {
                case "Type" when action: tr.Type = TakeWord(); break;
                case "Start" when action: tr.Start = TakeInt(); break;
                case "End" when action: tr.End = TakeInt(); break;
                case "PreviousAction" when action: tr.Previous = TakeName(); break;
                case "Unknown1" when action: tr.Unknown1 = TakeUInt(); break;
                case "Unknown2" when action: tr.Unknown2 = (ushort)TakeUInt(); break;
                case "Oneshot" when action: tr.OneShot = TakeBool(); break;
                case "Lazy" when !action: tr.Lazy = TakeBool(); break;
                case "Flags" when !action: tr.Flags = (ushort)TakeUInt(); break;
                case "Unknown" when !action: tr.Unknown2 = (ushort)TakeUInt(); break;
                case "Asset":
                    if (tr.HasAsset) throw Error("a trigger can only have one asset.", key);
                    tr.HasAsset = true;
                    TakeName();
                    Expect(Tk.LBracket);
                    tr.Asset = TakeUInt();
                    Expect(Tk.RBracket);
                    break;
                case "OverwriteParams": tr.Overwrite = ParseParams(); break;
                default: throw Error($"unknown trigger attribute '{key.Text}'.", key);
            }
        }
        if (!tr.HasAsset) throw Error("a trigger is missing its asset.");
    }

    void ParseSlots(PUser u)
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var slot = new PSlot { Name = TakeName() };
            Expect(Tk.LBrace);
            bool prefix = false;
            while (!TryTake(Tk.RBrace))
            {
                if (TryTake(Tk.At))
                {
                    if (TakeWord() == "Prefix") prefix = true;
                    continue;
                }
                var action = new PAction { Name = TakeName(), Prefix = prefix };
                prefix = false;
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace))
                {
                    var tr = new PTrigger { Guid = TakeUInt() };
                    ParseTriggerBody(tr, true);
                    action.Triggers.Add(tr);
                }
                slot.Actions.Add(action);
            }
            u.Slots.Add(slot);
        }
    }

    void ParseProperties(PUser u)
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var scope = Take();
            if (scope.Text is not ("Local" or "Global")) throw Error($"'{scope.Text}' is not a PropertyScope.", scope);
            Expect(Tk.Scope);
            var prop = new PProperty { Global = scope.Text == "Global", Name = TakeName() };
            Expect(Tk.LBrace);
            while (!TryTake(Tk.RBrace))
            {
                ExpectWord("if");
                var tr = new PTrigger
                {
                    Cond = ParseCondition(prop.Global ? SwitchKind.Global : SwitchKind.Local, prop.Name),
                };
                if (tr.Cond.IsDefault) throw Error("property triggers cannot use default conditions.");
                Expect(Tk.Arrow);
                tr.Guid = TakeUInt();
                ParseTriggerBody(tr, false);
                prop.Triggers.Add(tr);
            }
            u.Properties.Add(prop);
        }
    }

    void ParseAlways(PUser u)
    {
        Expect(Tk.LBrace);
        while (!TryTake(Tk.RBrace))
        {
            var tr = new PTrigger { Guid = TakeUInt() };
            ParseTriggerBody(tr, false);
            u.Always.Add(tr);
        }
    }

    // ---- conditions ----

    PCond ParseCondition(SwitchKind kind, string varName)
    {
        var first = Take();
        if (first.Kind == Tk.Word && first.Text == "_") return new PCond { IsDefault = true, Compare = CompareType.Equal };
        if (first.Kind != Tk.Lt) throw Error("expected <value>.", first);
        ExpectWord("value");
        Expect(Tk.Gt);
        var op = Take();
        var cond = new PCond
        {
            Compare = op.Kind switch
            {
                Tk.Eq => CompareType.Equal,
                Tk.Ne => CompareType.NotEqual,
                Tk.Lt => CompareType.LessThan,
                Tk.Gt => CompareType.GreaterThan,
                Tk.Le => CompareType.LessThanOrEqual,
                Tk.Ge => CompareType.GreaterThanOrEqual,
                _ => throw Error("expected a comparison operator.", op),
            },
        };
        var v = Take();
        switch (v.Kind)
        {
            case Tk.Word when v.Text == "true": cond.Value = true; break;
            case Tk.Word when v.Text == "false": cond.Value = false; break;
            case Tk.Word or Tk.Str:
            {
                string name = v.Kind == Tk.Str ? Unescape(v.Text) : v.Text;
                if (name != varName) throw Error("the name does not match the variable name.", v);
                Expect(Tk.Scope);
                cond.Value = TakeName();
                break;
            }
            case Tk.Dec or Tk.Hex or Tk.Bin: p--; cond.Value = TakeInt(); break;
            case Tk.Float: cond.Value = ParseFloat(v.Text); break;
            case Tk.Lt:
                ExpectWord("action");
                Expect(Tk.Gt);
                Expect(Tk.Scope);
                if (kind != SwitchKind.ActionSlot) throw Error("action conditions must be in an action switch.", v);
                cond.Value = TakeName();
                break;
            default: throw Error("expected a value to compare against.", v);
        }
        return cond;
    }

    // ---- asset call tables ----

    (SwitchKind Kind, string Name) ParseSwitchVariable()
    {
        var tok = Take();
        if (tok.Kind == Tk.Lt)
        {
            ExpectWord("null");
            Expect(Tk.Gt);
            return (SwitchKind.Null, "");
        }
        SwitchKind kind = tok.Text switch
        {
            "Local" => SwitchKind.Local,
            "Global" => SwitchKind.Global,
            "ActionSlot" => SwitchKind.ActionSlot,
            _ => throw Error($"unknown property scope '{tok.Text}'.", tok),
        };
        Expect(Tk.Scope);
        return (kind, TakeName());
    }

    /// <summary>Reads a call table's body, or returns null when the name is only a reference.</summary>
    PCall? ParseCall(PUser u, string key, uint guid)
    {
        if (Peek.Kind != Tk.LBrace) return null;
        Take();
        if (u.ByGuid.ContainsKey(guid)) throw Error($"asset call table {key}[0x{guid:x8}] is defined twice.");

        var call = new PCall { Key = key, Guid = guid };
        int unknown = -1;
        while (!TryTake(Tk.RBrace))
        {
            var k = Take();
            if (k.Kind == Tk.At)
            {
                if (TakeWord() != "Unknown") throw Error("unknown call table attribute.", k);
                Expect(Tk.Assign);
                unknown = TakeInt();
                continue;
            }
            Expect(Tk.Assign);
            switch (k.Text)
            {
                case "EmitCount": call.EmitCount = TakeInt(); break;
                case "Oneshot": call.OneShot = TakeBool(); break;
                case "NoPause": call.NoPause = TakeBool(); break;
                case "UserFlags": call.UserFlags = (byte)TakeUInt(); break;
                case "Execute":
                    if (call.Kind is not null) throw Error("a call table can only have one Execute.", k);
                    call.Kind = TakeWord();
                    call.Unknown = unknown;
                    u.ByGuid[guid] = call;
                    ParseContainer(u, call);
                    break;
                default: throw Error($"unknown call table attribute '{k.Text}'.", k);
            }
        }
        if (call.Kind is null) throw Error($"asset call table {key}[0x{guid:x8}] has no Execute.");
        return call;
    }

    PCall ParseChild(PUser u, string what)
    {
        var (name, guid) = TakeCallKey();
        return ParseCall(u, name, guid) ?? throw Error($"{what} children cannot be declared without a body: {name}[0x{guid:x8}].");
    }

    void ParseContainer(PUser u, PCall call)
    {
        switch (call.Kind)
        {
            case "Switch":
            {
                Expect(Tk.LParen);
                (call.Switch, call.Variable) = ParseSwitchVariable();
                Expect(Tk.RParen);
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace))
                {
                    Expect(Tk.LParen);
                    var cond = ParseCondition(call.Switch, call.Variable);
                    Expect(Tk.RParen);
                    Expect(Tk.Arrow);
                    var child = ParseChild(u, "Switch");
                    child.Cond = cond;
                    call.Children.Add(child);
                }
                break;
            }
            case "Random" or "RandomNoRepeat":
            {
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace))
                {
                    float weight = TakeFloat();
                    Expect(Tk.Arrow);
                    var child = ParseChild(u, call.Kind);
                    child.Weight = weight;
                    call.Children.Add(child);
                }
                break;
            }
            case "Blend":
            {
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace)) call.Children.Add(ParseChild(u, "Blend"));
                break;
            }
            case "BlendBy":
            {
                Expect(Tk.LParen);
                (call.Switch, call.Variable) = ParseSwitchVariable();
                if (call.Switch is SwitchKind.ActionSlot or SwitchKind.Null) throw Error("BlendBy containers need a local or global property.");
                Expect(Tk.RParen);
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace))
                {
                    Expect(Tk.LParen);
                    var (min, minOp) = ParseBlendBound("Min");
                    Expect(Tk.Comma);
                    var (max, maxOp) = ParseBlendBound("Max");
                    Expect(Tk.RParen);
                    Expect(Tk.Arrow);
                    var child = ParseChild(u, "BlendBy");
                    (child.Min, child.MinOp, child.Max, child.MaxOp) = (min, minOp, max, maxOp);
                    call.Children.Add(child);
                }
                break;
            }
            case "Sequence":
            {
                Expect(Tk.LBrace);
                bool force = false;
                uint forceValue = 0;
                while (!TryTake(Tk.RBrace))
                {
                    if (TryTake(Tk.At))
                    {
                        if (TakeWord() != "ForceContinue") throw Error("unknown sequence attribute.");
                        Expect(Tk.Assign);
                        forceValue = TakeUInt();
                        force = true;
                        continue;
                    }
                    var child = ParseChild(u, "Sequence");
                    if (force) { child.HasForceContinue = true; child.ForceContinue = forceValue; }
                    force = false;
                    call.Children.Add(child);
                }
                break;
            }
            case "Grid":
            {
                Expect(Tk.LParen);
                var (k1, n1) = ParseSwitchVariable();
                Expect(Tk.Comma);
                var (k2, n2) = ParseSwitchVariable();
                Expect(Tk.RParen);
                if (k1 is SwitchKind.ActionSlot or SwitchKind.Null || k2 is SwitchKind.ActionSlot or SwitchKind.Null)
                    throw Error("Grid containers need local or global properties.");
                (call.Prop1, call.Global1, call.Prop2, call.Global2) = (n1, k1 == SwitchKind.Global, n2, k2 == SwitchKind.Global);
                Expect(Tk.LBrace);
                while (!TryTake(Tk.RBrace))
                {
                    var section = Take();
                    Expect(Tk.LBrace);
                    if (section.Text == "Cases")
                    {
                        while (!TryTake(Tk.RBrace))
                        {
                            Expect(Tk.LParen);
                            string a = TakeName(); Expect(Tk.Scope); string v1 = TakeName();
                            Expect(Tk.Comma);
                            string b = TakeName(); Expect(Tk.Scope); string v2 = TakeName();
                            Expect(Tk.RParen);
                            if (a != n1 || b != n2) throw Error("the grid case does not match the property names.", section);
                            Expect(Tk.Arrow);
                            if (!call.Values1.Contains(v1)) call.Values1.Add(v1);
                            if (!call.Values2.Contains(v2)) call.Values2.Add(v2);
                            if (TryTake(Tk.Lt))
                            {
                                ExpectWord("null");
                                Expect(Tk.Gt);
                            }
                            else call.Cases.Add((v1, v2, TakeCallKey().Guid));
                        }
                    }
                    else if (section.Text == "Children")
                    {
                        while (!TryTake(Tk.RBrace)) call.Children.Add(ParseChild(u, "Grid"));
                    }
                    else throw Error($"unknown grid section '{section.Text}'.", section);
                }
                break;
            }
            case "Jump":
            {
                Expect(Tk.Arrow);
                if (TryTake(Tk.Lt))
                {
                    ExpectWord("null");
                    Expect(Tk.Gt);
                    break;
                }
                var (name, guid) = TakeCallKey();
                call.JumpGuid = guid;
                call.JumpInline = ParseCall(u, name, guid);
                break;
            }
            case "Asset":
                call.Params = ParseParams();
                break;
            default:
                throw Error($"unknown call table type '{call.Kind}'.");
        }
    }

    (float Value, BlendOp Op) ParseBlendBound(string type)
    {
        float value = 0;
        var op = BlendOp.None;
        Expect(Tk.LBracket);
        for (int i = 0; i < 2; i++)
        {
            string key = TakeWord();
            Expect(Tk.Colon);
            if (key == type) value = TakeFloat();
            else if (key == "Op") op = TakeEnum<BlendOp>();
            else TakeWord();
            if (i == 0) Expect(Tk.Comma);
        }
        Expect(Tk.RBracket);
        return (value, op);
    }

    // ---- laying the parsed tree out as the binary's tables ----

    ParamValue ToParamValue(string name, PValue raw, ParamDefine def)
    {
        switch (def.Type, raw)
        {
            case (ParamType.Int, PInt i): return new(RefType.Direct, i.Value);
            case (ParamType.Int, PBits b): return new(RefType.Bitflag, b.Value);
            case (ParamType.Float, PFloat x): return new(RefType.Direct, x.Value);
            case (ParamType.Float, PInt i): return new(RefType.Direct, (float)i.Value);
            case (ParamType.Float, PCurve c): return new(RefType.Curve, c.Value);
            case (ParamType.Float, PRandom r): return new(r.Ref, r.Value);
            case (ParamType.Bool, PBool b): return new(RefType.Direct, b.Value);
            case (ParamType.Enum, PEnum e): return new(RefType.Direct, e.Value);
            case (ParamType.String, PString s): return new(RefType.String, s.Value);
            case (ParamType.Custom, PArrange a): return new(RefType.ArrangeGroup, a.Value);
        }
        throw new FormatException($"Param {name} has a value that does not fit its {def.Type} define.");
    }

    ParamValue DefaultFor(ParamDefine def) => def.Type switch
    {
        ParamType.String => new(RefType.String, def.Default ?? ""),
        ParamType.Custom => new(RefType.ArrangeGroup, new List<ArrangeGroup>()),
        _ => new(RefType.Direct, def.Default),
    };

    List<KeyValuePair<string, ParamValue>> Ordered(List<KeyValuePair<string, PValue>> raw, List<ParamDefine> defs, Dictionary<string, ParamDefine> byName, bool fillDefaults)
    {
        foreach (var (name, _) in raw)
            if (!byName.ContainsKey(name)) throw new FormatException($"Unknown param '{name}'.");
        var result = new List<KeyValuePair<string, ParamValue>>();
        if (!fillDefaults && raw.Count == 0) return result;
        foreach (var def in defs)
        {
            int at = raw.FindIndex(r => r.Key == def.Name);
            if (at >= 0) result.Add(new(def.Name, ToParamValue(def.Name, raw[at].Value, def)));
            else if (fillDefaults) result.Add(new(def.Name, DefaultFor(def)));
        }
        return result;
    }

    Condition? ConditionFor(PCall parent, PCall child) => parent.Kind switch
    {
        "Switch" when child.Cond is { IsDefault: false } c => MakeSwitch(c, parent.Switch, ContainerType.Switch),
        "Random" => new RandomCondition { ParentType = ContainerType.Random, Weight = child.Weight },
        "RandomNoRepeat" => new RandomCondition { ParentType = ContainerType.Random2, Weight = child.Weight },
        "BlendBy" => new BlendCondition { ParentType = ContainerType.Blend, Min = child.Min, Max = child.Max, MinOp = child.MinOp, MaxOp = child.MaxOp },
        "Sequence" when child.HasForceContinue => new SequenceCondition { ParentType = ContainerType.Sequence, ForceContinue = (int)child.ForceContinue },
        _ => null,
    };

    static SwitchCondition MakeSwitch(PCond c, SwitchKind kind, ContainerType parentType) => new()
    {
        ParentType = parentType,
        Compare = c.Compare,
        Value = c.Value,
        PropertyType = c.Value switch { string => PropertyType.Enum, int => PropertyType.S32, float => PropertyType.F32, _ => PropertyType.Bool },
        IsGlobal = kind == SwitchKind.Global,
        IsAction = kind == SwitchKind.ActionSlot,
    };

    User Build(PUser pu)
    {
        var u = new User { NameHash = pu.Hash, Name = pu.Name, Unknown = pu.Unknown };
        foreach (var l in pu.Locals.Distinct().OrderBy(s => XLinkFile.Utf8.GetBytes(s), ByteComparer.Instance)) u.LocalProperties.Add(l);
        u.Params.AddRange(Ordered(pu.Params, f.UserParams, userDefs, true));

        // Containers' children are queued after their parent, so each container's children get consecutive indices.
        var order = new List<(PCall Call, PCall? Parent)>();
        var index = new Dictionary<PCall, int>();
        var queue = new Queue<(PCall, PCall?)>(pu.Roots.Select(r => (r, (PCall?)null)));
        while (queue.Count > 0)
        {
            var (call, parent) = queue.Dequeue();
            if (index.ContainsKey(call)) continue;
            index[call] = order.Count;
            order.Add((call, parent));
            if (call.Kind == "Jump") continue;
            foreach (var child in call.Children) queue.Enqueue((child, call));
        }

        int Resolve(uint guid)
        {
            if (!pu.ByGuid.TryGetValue(guid, out var target) || !index.TryGetValue(target, out int i))
                throw new FormatException($"No asset call table has the GUID 0x{guid:x8}.");
            return i;
        }

        foreach (var (call, parent) in order)
        {
            var a = new AssetCall
            {
                KeyName = call.Key,
                Flag = (ushort)(call.UserFlags << 8 | (call.OneShot ? 8 : 0) | (call.NoPause ? 0x10 : 0)),
                Duration = call.EmitCount,
                ParentIndex = parent is null ? -1 : index[parent],
                Guid = call.Guid,
            };
            if (parent is not null) a.Condition = ConditionFor(parent, call);
            if (call.Kind == "Asset")
            {
                a.Params = Ordered(call.Params, f.AssetParams, assetDefs, false);
            }
            else
            {
                var c = new Container();
                a.Container = c;
                switch (call.Kind)
                {
                    case "Switch":
                        c.Type = ContainerType.Switch;
                        c.WatchPropertyName = call.Variable;
                        c.WatchPropertyId = call.Unknown;
                        c.IsGlobal = call.Switch == SwitchKind.Global;
                        c.IsAction = call.Switch == SwitchKind.ActionSlot;
                        c.WatchPropertyIndex = call.Switch == SwitchKind.Local ? 0 : -1; // the writer assigns the real index
                        break;
                    case "Random": c.Type = ContainerType.Random; break;
                    case "RandomNoRepeat": c.Type = ContainerType.Random2; break;
                    case "Blend": c.Type = ContainerType.Blend; break;
                    case "BlendBy":
                        c.Type = ContainerType.Blend;
                        c.IsBlendBy = true;
                        c.WatchPropertyName = call.Variable;
                        c.WatchPropertyId = call.Unknown;
                        c.IsGlobal = call.Switch == SwitchKind.Global;
                        c.WatchPropertyIndex = call.Switch == SwitchKind.Local ? 0 : -1;
                        break;
                    case "Sequence": c.Type = ContainerType.Sequence; break;
                    case "Jump": c.Type = ContainerType.Jump; break;
                    case "Grid":
                        c.Type = ContainerType.Grid;
                        c.Property1Name = call.Prop1;
                        c.Property2Name = call.Prop2;
                        c.IsProperty1Global = call.Global1;
                        c.IsProperty2Global = call.Global2;
                        c.Values1.AddRange(call.Values1);
                        c.Values2.AddRange(call.Values2);
                        foreach (var v1 in call.Values1)
                            foreach (var v2 in call.Values2)
                            {
                                var hit = call.Cases.FirstOrDefault(x => x.V1 == v1 && x.V2 == v2);
                                if (hit == default) { c.Cells.Add(-1); continue; }
                                var child = call.Children.FirstOrDefault(ch => ch.Guid == hit.Guid)
                                    ?? throw new FormatException($"Asset call table 0x{hit.Guid:x8} does not exist in the grid; use a Jump container for a target elsewhere.");
                                c.Cells.Add(index[child]);
                            }
                        break;
                }

                if (call.Kind == "Jump")
                {
                    c.ChildrenStart = c.ChildrenEnd = call.JumpGuid is { } g ? Resolve(g) : -1;
                }
                else if (call.Children.Count > 0)
                {
                    c.ChildrenStart = index[call.Children[0]];
                    c.ChildrenEnd = index[call.Children[^1]];
                }
                else
                {
                    throw new FormatException($"{call.Key}[0x{call.Guid:x8}] is a {call.Kind} container without children.");
                }
            }
            u.AssetCalls.Add(a);
        }

        foreach (var slot in pu.Slots)
        {
            var s = new ActionSlot { Name = slot.Name };
            foreach (var action in slot.Actions)
            {
                var act = new Action { Name = action.Name, IsPrefix = action.Prefix };
                foreach (var tr in action.Triggers)
                {
                    ushort flag = (ushort)((tr.OneShot ? 1 : 0) | tr.Type switch
                    {
                        "FrameWindow" => 0,
                        "Always" => 4,
                        "OnLeave" => 8,
                        "Previous" => 0x10,
                        _ => throw new FormatException($"'{tr.Type}' is not an ActionTriggerType."),
                    });
                    var t = new Trigger
                    {
                        Guid = tr.Guid,
                        Unknown1 = (int)tr.Unknown1,
                        AssetCallIndex = Resolve(tr.Asset),
                        StartFrame = tr.Start,
                        EndFrame = tr.End,
                        Flag = flag,
                        OverwriteHash = (ushort)tr.Unknown2,
                        Overwrite = Overwrite(tr),
                    };
                    if (tr.Type == "Previous") t.PreviousActionName = tr.Previous;
                    act.Triggers.Add(t);
                }
                s.Actions.Add(act);
            }
            u.ActionSlots.Add(s);
        }

        foreach (var prop in pu.Properties)
        {
            var pr = new Property { WatchPropertyName = prop.Name, IsGlobal = prop.Global };
            var kind = prop.Global ? SwitchKind.Global : SwitchKind.Local;
            foreach (var tr in prop.Triggers)
                pr.Triggers.Add(new Trigger
                {
                    Guid = tr.Guid,
                    AssetCallIndex = Resolve(tr.Asset),
                    Flag = (ushort)(tr.Lazy ? 1 : 0),
                    OverwriteHash = (ushort)tr.Unknown2,
                    Condition = MakeSwitch(tr.Cond!, kind, ContainerType.Switch),
                    Overwrite = Overwrite(tr),
                });
            u.Properties.Add(pr);
        }

        foreach (var tr in pu.Always)
            u.AlwaysTriggers.Add(new Trigger
            {
                Guid = tr.Guid,
                AssetCallIndex = Resolve(tr.Asset),
                Flag = tr.Flags,
                OverwriteHash = (ushort)tr.Unknown2,
                Overwrite = Overwrite(tr),
            });
        return u;
    }

    List<KeyValuePair<string, ParamValue>>? Overwrite(PTrigger tr)
    {
        var list = Ordered(tr.Overwrite, f.TriggerParams, triggerDefs, false);
        return list.Count == 0 ? null : list;
    }
}
