using System.Buffers.Binary;

namespace XLinkSharp;

sealed class Buf(bool big, int ptrSize)
{
    byte[] d = new byte[256];
    public int Length { get; private set; }

    Span<byte> Take(int n)
    {
        if (Length + n > d.Length) Array.Resize(ref d, Math.Max(d.Length * 2, Length + n));
        var s = d.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public void U8(int v) => Take(1)[0] = (byte)v;
    public void Bool(bool v) => U8(v ? 1 : 0);
    public void U16(int v) { if (big) BinaryPrimitives.WriteUInt16BigEndian(Take(2), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)v); }
    public void U32(uint v) { if (big) BinaryPrimitives.WriteUInt32BigEndian(Take(4), v); else BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v); }
    public void S32(int v) => U32((uint)v);
    public void U64(ulong v) { if (big) BinaryPrimitives.WriteUInt64BigEndian(Take(8), v); else BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v); }
    public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
    public void Ptr(long v) { if (ptrSize == 8) U64((ulong)v); else U32((uint)v); }
    public void Zero(int n) => Take(n).Clear();
    public void Align(int a) => Zero((a - Length % a) % a);
    public void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));
    public void Str(string s) { Bytes(XLinkFile.Utf8.GetBytes(s)); U8(0); }
    public void PatchPtr(int at, long v)
    {
        var s = d.AsSpan(at);
        if (ptrSize == 8) { if (big) BinaryPrimitives.WriteUInt64BigEndian(s, (ulong)v); else BinaryPrimitives.WriteUInt64LittleEndian(s, (ulong)v); }
        else if (big) BinaryPrimitives.WriteUInt32BigEndian(s, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)v);
    }
    public ReadOnlySpan<byte> Span => d.AsSpan(0, Length);
    public string Key => Convert.ToHexString(Span);
}

sealed class XLinkWriter
{
    const uint None = 0xFFFFFFFF;

    readonly XLinkFile f;
    readonly bool big, wide;
    readonly int P;

    readonly HashSet<string> nameSet = [];
    readonly List<string> localNames, enumNames;
    readonly Dictionary<string, int> localIdx = [], enumIdx = [];
    readonly Dictionary<string, long> names = [];

    readonly Buf direct, random, curves, points, arrange, assetParams, overwrites, conditions;
    readonly Dictionary<uint, int> directMap = [];
    readonly Dictionary<ulong, int> randomMap = [];
    readonly Dictionary<string, int> curveMap = [];
    readonly Dictionary<string, long> arrangeMap = [], assetParamMap = [], overwriteMap = [], conditionMap = [];
    int numResParam, curveCount, pointCount;

    public XLinkWriter(XLinkFile file)
    {
        f = file;
        big = f.BigEndian;
        wide = f.Game == XLinkGame.Totk;
        P = wide ? 8 : 4;
        localNames = [.. f.LocalPropertyNames];
        enumNames = [.. f.LocalPropertyEnumNames];
        for (int i = localNames.Count - 1; i >= 0; i--) localIdx[localNames[i]] = i;
        for (int i = enumNames.Count - 1; i >= 0; i--) enumIdx[enumNames[i]] = i;
        direct = New(); random = New(); curves = New(); points = New(); arrange = New();
        assetParams = New(); overwrites = New(); conditions = New();
    }

    Buf New() => new(big, P);

    void Str(string s) => nameSet.Add(s);

    void Local(string s)
    {
        Str(s);
        if (localIdx.TryAdd(s, localNames.Count)) localNames.Add(s);
    }

    void Enum(string s)
    {
        Str(s);
        if (enumIdx.TryAdd(s, enumNames.Count)) enumNames.Add(s);
    }

    void Collect(List<KeyValuePair<string, ParamValue>>? ps)
    {
        if (ps is null) return;
        foreach (var (_, v) in ps)
        {
            switch (v.Value)
            {
                case string s: Str(s); break;
                case Curve c: if (c.IsGlobal) Str(c.PropertyName); else Local(c.PropertyName); break;
                case List<ArrangeGroup> a: foreach (var g in a) Str(g.Name); break;
            }
        }
    }

    void Collect(Condition? c)
    {
        if (c is SwitchCondition { PropertyType: PropertyType.Enum } s)
        {
            if (s.IsGlobal || s.IsAction) Str((string)s.Value);
            else Enum((string)s.Value);
        }
    }

    void Watch(string name, bool global)
    {
        if (global || name.Length == 0) Str(name);
        else Local(name);
    }

    void Collect(User u)
    {
        if (u.Name is not null) Str(u.Name);
        foreach (var l in u.LocalProperties) Local(l);
        Collect(u.Params);
        foreach (var a in u.AssetCalls)
        {
            Str(a.KeyName);
            Collect(a.Params);
            Collect(a.Condition);
            if (a.Container is not { } c) continue;
            if (c.HasWatchProperty) Watch(c.WatchPropertyName ?? "", c.IsGlobal || c.IsAction);
            else if (c.Type == ContainerType.Grid)
            {
                Watch(c.Property1Name ?? "", c.IsProperty1Global);
                Watch(c.Property2Name ?? "", c.IsProperty2Global);
                foreach (var v in c.Values1) if (c.IsProperty1Global) Str(v); else Enum(v);
                foreach (var v in c.Values2) if (c.IsProperty2Global) Str(v); else Enum(v);
            }
        }
        foreach (var s in u.ActionSlots)
        {
            Str(s.Name);
            foreach (var a in s.Actions)
            {
                Str(a.Name);
                foreach (var t in a.Triggers)
                {
                    if (t.IsPrevious) Str(t.PreviousActionName ?? "");
                    Collect(t.Overwrite);
                }
            }
        }
        foreach (var p in u.Properties)
        {
            Watch(p.WatchPropertyName, p.IsGlobal);
            foreach (var t in p.Triggers) { Collect(t.Condition); Collect(t.Overwrite); }
        }
        foreach (var t in u.AlwaysTriggers) Collect(t.Overwrite);
    }

    int Direct(uint v)
    {
        if (!directMap.TryGetValue(v, out var i)) { directMap[v] = i = directMap.Count; direct.U32(v); }
        return i;
    }

    static uint Bits(object? v) => v switch
    {
        int i => (uint)i,
        uint u => u,
        float x => BitConverter.SingleToUInt32Bits(x),
        bool b => b ? 1u : 0u,
        _ => throw new InvalidDataException($"Unsupported direct value {v ?? "null"}."),
    };

    int Random(RandomRange r)
    {
        ulong key = (ulong)BitConverter.SingleToUInt32Bits(r.Min) << 32 | BitConverter.SingleToUInt32Bits(r.Max);
        if (!randomMap.TryGetValue(key, out var i)) { randomMap[key] = i = randomMap.Count; random.F32(r.Min); random.F32(r.Max); }
        return i;
    }

    void CurveBody(Buf b, Curve c, int start)
    {
        b.U16(start);
        b.U16(c.Points.Count);
        b.U16(c.Type);
        b.U16(c.IsGlobal ? 1 : 0);
        b.Ptr(names[c.PropertyName]);
        b.S32(c.Unknown);
        b.U16(c.IsGlobal ? -1 : localIdx[c.PropertyName]);
        b.U16(c.UpdateType);
    }

    int CurveIndex(Curve c)
    {
        var k = New();
        CurveBody(k, c, 0);
        foreach (var p in c.Points) { k.F32(p.X); k.F32(p.Y); }
        if (curveMap.TryGetValue(k.Key, out var i)) return i;
        curveMap[k.Key] = i = curveCount++;
        CurveBody(curves, c, pointCount);
        foreach (var p in c.Points) { points.F32(p.X); points.F32(p.Y); }
        pointCount += c.Points.Count;
        return i;
    }

    long Arrange(List<ArrangeGroup> a)
    {
        var k = New();
        k.S32(a.Count);
        foreach (var g in a)
        {
            k.Ptr(names[g.Name]);
            k.U8((byte)g.LimitType);
            k.U8((byte)g.Threshold);
            k.Bool(g.IncludeFading);
            k.Zero(P - 3);
        }
        return Pool(k, arrange, arrangeMap);
    }

    static long Pool(Buf k, Buf pool, Dictionary<string, long> map)
    {
        if (map.TryGetValue(k.Key, out var off)) return off;
        map[k.Key] = off = pool.Length;
        pool.Bytes(k.Span);
        return off;
    }

    uint Pack(ParamValue v)
    {
        long x = v.RefType switch
        {
            RefType.Direct => Direct(Bits(v.Value)),
            RefType.Bitflag => Direct(Bits(v.Value)),
            RefType.String => names[(string)v.Value!],
            RefType.Curve => CurveIndex((Curve)v.Value!),
            RefType.ArrangeGroup => Arrange((List<ArrangeGroup>)v.Value!),
            _ => Random((RandomRange)v.Value!),
        };
        if (x > 0xFFFFFF) throw new InvalidDataException("Param reference out of range.");
        return (uint)v.RefType << 24 | (uint)x;
    }

    ParamValue Default(ParamDefine def) => def.Type switch
    {
        ParamType.String => new(RefType.String, def.Default ?? ""),
        ParamType.Custom => new(RefType.ArrangeGroup, new List<ArrangeGroup>()),
        _ => new(RefType.Direct, def.Default),
    };

    (ulong Mask, List<uint> Values) Masked(List<KeyValuePair<string, ParamValue>> ps, List<ParamDefine> defs, string what)
    {
        var map = new Dictionary<string, ParamValue>();
        foreach (var (k, v) in ps)
            if (!map.TryAdd(k, v)) throw new InvalidDataException($"Duplicate {what} param '{k}'.");
        ulong mask = 0;
        var values = new List<uint>();
        for (int i = 0; i < defs.Count; i++)
        {
            if (!map.Remove(defs[i].Name, out var v)) continue;
            mask |= 1UL << i;
            values.Add(Pack(v));
        }
        if (map.Count > 0) throw new InvalidDataException($"Unknown {what} param '{map.Keys.First()}'.");
        return (mask, values);
    }

    long AssetParams(List<KeyValuePair<string, ParamValue>> ps)
    {
        var (mask, values) = Masked(ps, f.AssetParams, "asset");
        var k = New();
        k.U64(mask);
        foreach (var v in values) k.U32(v);
        int before = assetParamMap.Count;
        long off = Pool(k, assetParams, assetParamMap);
        if (assetParamMap.Count != before) numResParam += values.Count;
        return off;
    }

    long Overwrite(List<KeyValuePair<string, ParamValue>>? ps)
    {
        if (ps is null) return None;
        var (mask, values) = Masked(ps, f.TriggerParams, "trigger");
        var k = New();
        k.U32((uint)mask);
        foreach (var v in values) k.U32(v);
        return Pool(k, overwrites, overwriteMap);
    }

    long Cond(Condition? c)
    {
        if (c is null) return None;
        var k = New();
        k.U32((byte)c.ParentType);
        switch (c)
        {
            case SwitchCondition s:
            {
                bool isEnum = s.PropertyType == PropertyType.Enum;
                uint value = isEnum ? 0 : Bits(s.Value);
                if (wide)
                {
                    k.U8((byte)s.PropertyType);
                    k.U8((byte)s.Compare);
                    k.Bool(s.IsSolved);
                    k.Bool(s.IsGlobal);
                    if (isEnum)
                    {
                        var name = (string)s.Value;
                        k.U32(s.IsAction ? XLinkFile.Crc32(name) : s.IsGlobal ? None : (uint)enumIdx[name]);
                        k.U32(0);
                        k.Ptr(names[name]);
                    }
                    else
                    {
                        k.U32(None);
                        k.U32(value);
                    }
                }
                else
                {
                    k.U32((byte)s.PropertyType);
                    k.U32((byte)s.Compare);
                    if (isEnum) { k.U32((uint)names[(string)s.Value]); k.U16(s.IsGlobal ? -1 : enumIdx[(string)s.Value]); }
                    else { k.U32(value); k.U16(-1); }
                    k.Bool(s.IsSolved);
                    k.Bool(s.IsGlobal);
                }
                break;
            }
            case RandomCondition r: k.F32(r.Weight); break;
            case BlendCondition b: k.F32(b.Min); k.F32(b.Max); k.U8((byte)b.MinOp); k.U8((byte)b.MaxOp); k.Zero(2); break;
            case SequenceCondition q: k.S32(q.ForceContinue); break;
        }
        return Pool(k, conditions, conditionMap);
    }

    int ContainerSize(Container c) => c.HasWatchProperty ? (wide ? 0x20 : 0x18)
        : c.Type == ContainerType.Grid ? 0x28 + 4 * (c.Values1.Count + c.Values2.Count + c.Cells.Count)
        : wide ? 0x10 : 0xC;

    int PropIndex(string? name, bool global) => !global && name is { Length: > 0 } ? localIdx[name] : -1;

    void WriteContainer(Buf b, Container c)
    {
        if (wide)
        {
            b.U8((byte)c.Type);
            b.Bool(c.IsBlendBy);
            b.Zero(2);
            b.S32(c.ChildrenStart);
            b.S32(c.ChildrenEnd);
            b.Zero(4);
        }
        else
        {
            b.U32((byte)c.Type);
            b.S32(c.ChildrenStart);
            b.S32(c.ChildrenEnd);
        }
        if (c.HasWatchProperty)
        {
            var name = c.WatchPropertyName ?? "";
            b.Ptr(names[name]);
            b.S32(c.WatchPropertyId);
            b.U16(PropIndex(name, c.IsGlobal || c.IsAction));
            b.Bool(c.IsGlobal);
            b.Bool(c.IsAction);
        }
        else if (c.Type == ContainerType.Grid)
        {
            if (!wide) throw new InvalidDataException("Grid containers require TotK.");
            if (c.Cells.Count != c.Values1.Count * c.Values2.Count) throw new InvalidDataException("Grid cell count must equal Values1 x Values2.");
            b.Ptr(names[c.Property1Name ?? ""]);
            b.Ptr(names[c.Property2Name ?? ""]);
            b.U16(PropIndex(c.Property1Name, c.IsProperty1Global));
            b.U16(PropIndex(c.Property2Name, c.IsProperty2Global));
            b.U16((c.IsProperty1Global ? 1 : 0) | (c.IsProperty2Global ? 2 : 0));
            b.U8(c.Values1.Count);
            b.U8(c.Values2.Count);
            foreach (var v in c.Values1) b.U32(c.IsProperty1Global ? (uint)names[v] : (uint)enumIdx[v]);
            foreach (var v in c.Values2) b.U32(c.IsProperty2Global ? (uint)names[v] : (uint)enumIdx[v]);
            foreach (var x in c.Cells) b.S32(x);
        }
    }

    (int Start, int End) Range(ref int cursor, int count)
    {
        if (count == 0) return (-1, 0);
        var r = (cursor, cursor + count - 1);
        cursor += count;
        return r;
    }

    Buf WriteUser(User u)
    {
        var b = New();
        int cz = wide ? 0x30 : 0x20;
        int numAction = u.ActionSlots.Sum(s => s.Actions.Count);
        int numActionTrig = u.ActionSlots.Sum(s => s.Actions.Sum(a => a.Triggers.Count));
        int numPropTrig = u.Properties.Sum(p => p.Triggers.Count);

        b.U32(u.IsSetup);
        if (wide) { b.U16(u.LocalProperties.Count); b.U16(u.Unknown); }
        else b.S32(u.LocalProperties.Count);
        b.S32(u.AssetCalls.Count);
        b.S32(u.NumAsset);
        b.S32(u.NumRandomContainer2);
        b.S32(u.ActionSlots.Count);
        b.S32(numAction);
        b.S32(numActionTrig);
        b.S32(u.Properties.Count);
        b.S32(numPropTrig);
        b.S32(u.AlwaysTriggers.Count);
        if (wide) b.Zero(4);
        int trigPos = b.Length;
        b.Ptr(0);

        foreach (var l in u.LocalProperties) b.Ptr(names[l]);
        var userParams = u.Params.ToDictionary(p => p.Key, p => p.Value);
        foreach (var def in f.UserParams) b.U32(Pack(userParams.GetValueOrDefault(def.Name) ?? Default(def)));
        foreach (var id in u.SortedAssetIds()) b.U16(id);
        b.Align(4);

        long containerOff = 0;
        short assetId = 0;
        foreach (var a in u.AssetCalls)
        {
            b.Ptr(names[a.KeyName]);
            b.U16(a.IsContainer ? -1 : assetId++);
            b.U16((a.Flag & ~1) | (a.IsContainer ? 1 : 0));
            b.S32(a.Duration);
            b.S32(a.ParentIndex);
            b.U32(a.Guid);
            b.U32(XLinkFile.Crc32(a.KeyName));
            if (wide) b.Zero(4);
            if (a.Container is { } c) { b.Ptr(containerOff); containerOff += ContainerSize(c); }
            else b.Ptr(AssetParams(a.Params ?? []));
            b.Ptr(Cond(a.Condition));
        }
        foreach (var a in u.AssetCalls)
            if (a.Container is { } c) WriteContainer(b, c);

        b.PatchPtr(trigPos, b.Length);
        int cursor = 0;
        foreach (var s in u.ActionSlots)
        {
            b.Ptr(names[s.Name]);
            var (a0, a1) = Range(ref cursor, s.Actions.Count);
            b.U16(a0);
            b.U16(a1);
            if (wide) b.Zero(4);
        }
        cursor = 0;
        foreach (var a in u.ActionSlots.SelectMany(s => s.Actions))
        {
            b.Ptr(names[a.Name]);
            var (t0, t1) = Range(ref cursor, a.Triggers.Count);
            if (wide) { b.U16(t0); b.Bool(a.IsPrefix); b.Zero(1); b.S32(t1); }
            else { b.S32(t0); b.S32(t1); }
        }
        foreach (var t in u.ActionSlots.SelectMany(s => s.Actions).SelectMany(a => a.Triggers))
        {
            b.U32(t.Guid);
            if (wide) b.S32(t.Unknown1);
            b.Ptr((long)t.AssetCallIndex * cz);
            if (t.IsPrevious) b.Ptr(names[t.PreviousActionName ?? ""]);
            else { b.S32(t.StartFrame); if (wide) b.Zero(4); }
            b.S32(t.EndFrame);
            b.U16(t.Flag);
            b.U16(t.OverwriteHash);
            b.Ptr(Overwrite(t.Overwrite));
        }
        cursor = 0;
        foreach (var p in u.Properties)
        {
            b.Ptr(names[p.WatchPropertyName]);
            b.U32(p.IsGlobal ? 1u : 0u);
            var (t0, t1) = Range(ref cursor, p.Triggers.Count);
            b.S32(t0);
            b.S32(t1);
            if (wide) b.Zero(4);
        }
        foreach (var t in u.Properties.SelectMany(p => p.Triggers))
        {
            if (t.Condition is null) throw new InvalidDataException("Property triggers require a condition.");
            b.U32(t.Guid);
            if (wide)
            {
                b.U16(t.Flag); b.U16(t.OverwriteHash);
                b.Ptr((long)t.AssetCallIndex * cz);
                b.Ptr(Cond(t.Condition));
            }
            else
            {
                b.Ptr((long)t.AssetCallIndex * cz);
                b.Ptr(Cond(t.Condition));
                b.U16(t.Flag); b.U16(t.OverwriteHash);
            }
            b.Ptr(Overwrite(t.Overwrite));
        }
        foreach (var t in u.AlwaysTriggers)
        {
            b.U32(t.Guid);
            if (wide) { b.U16(t.Flag); b.U16(t.OverwriteHash); b.Ptr((long)t.AssetCallIndex * cz); }
            else { b.Ptr((long)t.AssetCallIndex * cz); b.U16(t.Flag); b.U16(t.OverwriteHash); }
            b.Ptr(Overwrite(t.Overwrite));
        }
        return b;
    }

    Buf WriteParamDefines()
    {
        var all = f.UserParams.Concat(f.AssetParams).Concat(f.TriggerParams).ToList();
        var pool = all.Select(p => p.Name).Concat(all.Where(p => p.Type == ParamType.String).Select(p => (string)(p.Default ?? "")))
            .Distinct().Select(s => XLinkFile.Utf8.GetBytes(s)).Order(ByteComparer.Instance).ToList();
        var offs = new Dictionary<string, long>();
        long poolSize = 0;
        foreach (var s in pool) { offs[XLinkFile.Utf8.GetString(s)] = poolSize; poolSize += s.Length + 1; }

        var b = New();
        int head = (0x14 + P - 1) / P * P;
        b.U32((uint)(head + all.Count * 3 * P + (poolSize + P - 1) / P * P));
        b.S32(f.UserParams.Count);
        b.S32(f.AssetParams.Count);
        b.S32(f.NumCustomAssetParam);
        b.S32(f.TriggerParams.Count);
        b.Align(P);
        foreach (var p in all)
        {
            b.Ptr(offs[p.Name]);
            b.U32((uint)p.Type);
            if (wide) b.Zero(4);
            switch (p.Type)
            {
                case ParamType.Int: if (wide) b.U64((ulong)(long)Convert.ToInt32(p.Default)); else b.S32(Convert.ToInt32(p.Default)); break;
                case ParamType.Float: if (wide) b.U64((ulong)BitConverter.DoubleToInt64Bits(Convert.ToSingle(p.Default))); else b.F32(Convert.ToSingle(p.Default)); break;
                case ParamType.Bool: b.Ptr(p.Default is true ? 1 : 0); break;
                case ParamType.Enum: b.Ptr(Convert.ToUInt32(p.Default)); break;
                case ParamType.String: b.Ptr(offs[(string)(p.Default ?? "")]); break;
                default: b.Ptr(0); break;
            }
        }
        foreach (var s in pool) { b.Bytes(s); b.U8(0); }
        b.Align(P);
        return b;
    }

    public byte[] Write()
    {
        var users = f.Users.OrderBy(u => u.NameHash).ToList();
        foreach (var u in users) Collect(u);
        foreach (var l in localNames) Str(l);
        foreach (var e in enumNames) Str(e);

        var sorted = nameSet.Select(s => XLinkFile.Utf8.GetBytes(s)).Order(ByteComparer.Instance).ToList();
        long namePool = 0;
        foreach (var s in sorted) { names[XLinkFile.Utf8.GetString(s)] = namePool; namePool += s.Length + 1; }

        var userBufs = users.Select(WriteUser).ToList();
        var pdt = WriteParamDefines();

        int headerSize = wide ? 0x60 : 0x48;
        long Align(long x) => (x + P - 1) / P * P;
        long hashes = headerSize;
        long offsets = Align(hashes + 4 * users.Count);
        long pdtPos = Align(offsets + P * users.Count);
        long assetParamPos = Align(pdtPos + pdt.Length);
        long overwritePos = assetParamPos + assetParams.Length;
        long localPos = overwritePos + overwrites.Length;
        long directPos = localPos + P * (localNames.Count + enumNames.Count);
        long exPos = directPos + direct.Length + random.Length + curves.Length + points.Length;
        long userPos = exPos + arrange.Length;
        long condPos = Align(userPos + userBufs.Sum(x => (long)x.Length));
        long namePos = Align(condPos + conditions.Length);
        long size = namePos + Align(namePool);
        if (size > uint.MaxValue) throw new InvalidDataException("XLNK file too large.");

        var o = New();
        o.Bytes("XLNK"u8);
        o.U32((uint)size);
        o.U32(f.Version);
        o.S32(numResParam);
        o.S32(assetParamMap.Count);
        o.S32(overwriteMap.Count);
        o.Ptr(overwritePos);
        o.Ptr(localPos);
        o.S32(localNames.Count);
        o.S32(enumNames.Count);
        o.S32(directMap.Count);
        o.S32(randomMap.Count);
        o.S32(curveCount);
        o.S32(pointCount);
        o.Align(P);
        o.Ptr(exPos);
        o.S32(users.Count);
        o.Align(P);
        o.Ptr(condPos);
        o.Ptr(namePos);
        foreach (var u in users) o.U32(u.NameHash);
        o.Align(P);
        long cur = userPos;
        foreach (var ub in userBufs) { o.Ptr(cur); cur += ub.Length; }
        o.Align(P);
        o.Bytes(pdt.Span);
        o.Align(P);
        o.Bytes(assetParams.Span);
        o.Bytes(overwrites.Span);
        foreach (var l in localNames) o.Ptr(names[l]);
        foreach (var e in enumNames) o.Ptr(names[e]);
        o.Bytes(direct.Span);
        o.Bytes(random.Span);
        o.Bytes(curves.Span);
        o.Bytes(points.Span);
        o.Bytes(arrange.Span);
        foreach (var ub in userBufs) o.Bytes(ub.Span);
        o.Align(P);
        o.Bytes(conditions.Span);
        o.Align(P);
        foreach (var s in sorted) { o.Bytes(s); o.U8(0); }
        o.Align(P);
        if (o.Length != size) throw new InvalidOperationException($"XLNK layout mismatch: wrote 0x{o.Length:X}, expected 0x{size:X}.");
        return o.Span.ToArray();
    }
}
