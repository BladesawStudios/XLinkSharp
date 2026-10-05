using System.Buffers.Binary;

namespace XLinkSharp;

sealed class XLinkReader(byte[] d)
{
    const uint None = 0xFFFFFFFF;

    readonly XLinkFile f = new();
    bool big, wide;
    int P;
    long nameTable, condTable, exRegion, assetParamTable, overwriteTable, directTable, randomTable, curveTable, curvePoints;
    readonly Dictionary<long, string> strings = [];

    ushort U16(long at) => big ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at));
    short S16(long at) => (short)U16(at);
    uint U32(long at) => big ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at));
    int S32(long at) => (int)U32(at);
    ulong U64(long at) => big ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)at));
    float F32(long at) => BitConverter.Int32BitsToSingle(S32(at));
    long Ptr(long at) => wide ? (long)U64(at) : U32(at);
    long Align(long x) => (x + P - 1) / P * P;

    string CStr(long at)
    {
        int e = Array.IndexOf(d, (byte)0, (int)at);
        return XLinkFile.Utf8.GetString(d, (int)at, (e < 0 ? d.Length : e) - (int)at);
    }

    string Name(long pos) => strings.TryGetValue(pos, out var s) ? s : strings[pos] = CStr(nameTable + pos);

    public XLinkFile Read()
    {
        if (d.Length < 0x48 || !d.AsSpan(0, 4).SequenceEqual("XLNK"u8)) throw new InvalidDataException("Not an XLNK file.");
        big = d[8] == 0 && d[11] != 0;
        f.BigEndian = big;
        (f.Game, f.Module) = U32(8) switch
        {
            0x1E => (XLinkGame.BotW, ModuleType.ELink),
            0x1C => (XLinkGame.BotW, ModuleType.SLink),
            0x24 => (XLinkGame.Totk, ModuleType.ELink),
            0x21 => (XLinkGame.Totk, ModuleType.SLink),
            var v => throw new InvalidDataException($"Unsupported XLNK version 0x{v:X}."),
        };
        wide = f.Game == XLinkGame.Totk;
        P = wide ? 8 : 4;

        long h = 0x18;
        overwriteTable = Ptr(h);
        long localNames = Ptr(h + P);
        long c = h + 2 * P;
        int numLocal = S32(c), numEnum = S32(c + 4), numDirect = S32(c + 8), numRandom = S32(c + 0xC), numCurve = S32(c + 0x10);
        c = Align(c + 0x18);
        exRegion = Ptr(c);
        int numUser = S32(c + P);
        c = Align(c + P + 4);
        condTable = Ptr(c);
        nameTable = Ptr(c + P);
        long hashes = c + 2 * P;
        long offsets = Align(hashes + numUser * 4);

        long pdt = Align(offsets + numUser * P);
        ReadParamDefines(pdt);
        assetParamTable = Align(pdt + U32(pdt));

        for (int i = 0; i < numLocal; i++) f.LocalPropertyNames.Add(Name(Ptr(localNames + i * P)));
        for (int i = 0; i < numEnum; i++) f.LocalPropertyEnumNames.Add(Name(Ptr(localNames + (numLocal + i) * P)));
        directTable = localNames + (numLocal + numEnum) * P;
        randomTable = directTable + numDirect * 4;
        curveTable = randomTable + numRandom * 8;
        curvePoints = curveTable + numCurve * (wide ? 0x18 : 0x14);

        var userNames = new Dictionary<uint, string>();
        for (long at = nameTable; at < d.Length;)
        {
            var s = CStr(at);
            userNames.TryAdd(XLinkFile.Crc32(s), s);
            at += XLinkFile.Utf8.GetByteCount(s) + 1;
        }

        for (int i = 0; i < numUser; i++)
        {
            var u = ReadUser(Ptr(offsets + i * P));
            u.NameHash = U32(hashes + i * 4);
            u.Name = userNames.GetValueOrDefault(u.NameHash);
            f.Users.Add(u);
        }
        return f;
    }

    void ReadParamDefines(long pdt)
    {
        int nu = S32(pdt + 4), na = S32(pdt + 8), nt = S32(pdt + 0x10);
        f.NumCustomAssetParam = S32(pdt + 0xC);
        int ds = 3 * P;
        long defs = Align(pdt + 0x14);
        long pool = defs + (nu + na + nt) * ds;
        ParamDefine Def(long at)
        {
            var type = (ParamType)U32(at + P);
            long v = at + 2 * P;
            object? def = type switch
            {
                ParamType.Int => wide ? (int)(long)U64(v) : S32(v),
                ParamType.Float => wide ? (float)BitConverter.Int64BitsToDouble((long)U64(v)) : F32(v),
                ParamType.Bool => Ptr(v) != 0,
                ParamType.Enum => (uint)Ptr(v),
                ParamType.String => CStr(pool + Ptr(v)),
                _ => null,
            };
            return new ParamDefine(CStr(pool + Ptr(at)), type, def);
        }
        for (int i = 0; i < nu; i++) f.UserParams.Add(Def(defs + i * ds));
        for (int i = 0; i < na; i++) f.AssetParams.Add(Def(defs + (nu + i) * ds));
        for (int i = 0; i < nt; i++) f.TriggerParams.Add(Def(defs + (nu + na + i) * ds));
    }

    ParamValue Value(uint raw, ParamType type)
    {
        var rt = (RefType)(raw >> 24);
        int x = (int)(raw & 0xFFFFFF);
        object? v = rt switch
        {
            RefType.Direct => type switch
            {
                ParamType.Float => F32(directTable + x * 4),
                ParamType.Bool => U32(directTable + x * 4) != 0,
                ParamType.Enum => U32(directTable + x * 4),
                _ => S32(directTable + x * 4),
            },
            RefType.Bitflag => U32(directTable + x * 4),
            RefType.String => Name(x),
            RefType.Curve => ReadCurve(curveTable + x * (wide ? 0x18 : 0x14)),
            RefType.ArrangeGroup => ReadArrange(exRegion + x),
            _ => new RandomRange(F32(randomTable + x * 8), F32(randomTable + x * 8 + 4)),
        };
        return new ParamValue(rt, v);
    }

    Curve ReadCurve(long at)
    {
        int start = U16(at), n = U16(at + 2);
        var c = new Curve { Type = U16(at + 4), IsGlobal = U16(at + 6) != 0, PropertyName = Name(Ptr(at + 8)) };
        long t = at + 8 + P;
        c.Unknown = S32(t);
        c.UpdateType = S16(t + 6);
        for (int i = 0; i < n; i++) c.Points.Add(new(F32(curvePoints + (start + i) * 8), F32(curvePoints + (start + i) * 8 + 4)));
        return c;
    }

    List<ArrangeGroup> ReadArrange(long at)
    {
        int n = S32(at);
        var res = new List<ArrangeGroup>(n);
        for (int i = 0; i < n; i++)
        {
            long p = at + 4 + i * 2 * P;
            res.Add(new(Name(Ptr(p)), (LimitType)d[p + P], (sbyte)d[p + P + 1], d[p + P + 2] != 0));
        }
        return res;
    }

    List<KeyValuePair<string, ParamValue>> MaskedParams(ulong mask, long at, List<ParamDefine> defs)
    {
        var res = new List<KeyValuePair<string, ParamValue>>();
        int k = 0;
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0) continue;
            if (i >= defs.Count) throw new InvalidDataException($"Param bit {i} has no define.");
            res.Add(new(defs[i].Name, Value(U32(at + k++ * 4), defs[i].Type)));
        }
        return res;
    }

    List<KeyValuePair<string, ParamValue>>? Overwrite(long ow) =>
        ow == None ? null : MaskedParams(U32(overwriteTable + ow), overwriteTable + ow + 4, f.TriggerParams);

    Condition ReadCondition(long pos)
    {
        long at = condTable + pos;
        var type = (ContainerType)U32(at);
        switch (type)
        {
            case ContainerType.Switch:
            {
                var c = new SwitchCondition { ParentType = type };
                uint value;
                if (wide)
                {
                    c.PropertyType = (PropertyType)d[at + 4];
                    c.Compare = (CompareType)d[at + 5];
                    c.IsSolved = d[at + 6] != 0;
                    c.IsGlobal = d[at + 7] != 0;
                    value = U32(at + 0xC);
                    if (c.PropertyType == PropertyType.Enum)
                    {
                        var name = Name(Ptr(at + 0x10));
                        uint key = U32(at + 8);
                        c.Value = name;
                        c.IsAction = !c.IsGlobal && key == XLinkFile.Crc32(name)
                            && !(key < f.LocalPropertyEnumNames.Count && f.LocalPropertyEnumNames[(int)key] == name);
                        return c;
                    }
                }
                else
                {
                    c.PropertyType = (PropertyType)U32(at + 4);
                    c.Compare = (CompareType)U32(at + 8);
                    c.IsSolved = d[at + 0x12] != 0;
                    c.IsGlobal = d[at + 0x13] != 0;
                    value = U32(at + 0xC);
                    if (c.PropertyType == PropertyType.Enum)
                    {
                        c.Value = c.IsGlobal ? Name(value) : f.LocalPropertyEnumNames[S16(at + 0x10)];
                        return c;
                    }
                }
                c.Value = c.PropertyType switch
                {
                    PropertyType.F32 or PropertyType.F32Alt => BitConverter.UInt32BitsToSingle(value),
                    PropertyType.Bool => value != 0,
                    _ => (int)value,
                };
                return c;
            }
            case ContainerType.Random or ContainerType.Random2:
                return new RandomCondition { ParentType = type, Weight = F32(at + 4) };
            case ContainerType.Blend:
                return new BlendCondition { ParentType = type, Min = F32(at + 4), Max = F32(at + 8), MinOp = (BlendOp)d[at + 0xC], MaxOp = (BlendOp)d[at + 0xD] };
            case ContainerType.Sequence:
                return new SequenceCondition { ParentType = type, ForceContinue = S32(at + 4) };
            default:
                return new EmptyCondition { ParentType = type };
        }
    }

    Container ReadContainer(long cp)
    {
        var ct = new Container();
        if (wide)
        {
            ct.Type = (ContainerType)d[cp];
            ct.IsBlendBy = d[cp + 1] != 0;
            ct.ChildrenStart = S32(cp + 4);
            ct.ChildrenEnd = S32(cp + 8);
        }
        else
        {
            ct.Type = (ContainerType)U32(cp);
            ct.ChildrenStart = S32(cp + 4);
            ct.ChildrenEnd = S32(cp + 8);
        }
        long x = cp + (wide ? 0x10 : 0xC);
        if (ct.HasWatchProperty)
        {
            ct.WatchPropertyName = Name(Ptr(x));
            ct.WatchPropertyId = S32(x + P);
            ct.IsGlobal = d[x + P + 6] != 0;
            ct.IsAction = d[x + P + 7] != 0;
        }
        else if (ct.Type == ContainerType.Grid)
        {
            int flags = U16(x + 2 * P + 4), n1 = d[x + 2 * P + 6], n2 = d[x + 2 * P + 7];
            ct.Property1Name = Name(Ptr(x));
            ct.Property2Name = Name(Ptr(x + P));
            ct.IsProperty1Global = (flags & 1) != 0;
            ct.IsProperty2Global = (flags & 2) != 0;
            long v = x + 2 * P + 8;
            for (int i = 0; i < n1; i++, v += 4) ct.Values1.Add(ct.IsProperty1Global ? Name(U32(v)) : f.LocalPropertyEnumNames[S32(v)]);
            for (int i = 0; i < n2; i++, v += 4) ct.Values2.Add(ct.IsProperty2Global ? Name(U32(v)) : f.LocalPropertyEnumNames[S32(v)]);
            for (int i = 0; i < n1 * n2; i++, v += 4) ct.Cells.Add(S32(v));
        }
        return ct;
    }

    User ReadUser(long at)
    {
        var u = new User { IsSetup = U32(at) };
        int numLocal;
        if (wide) { numLocal = U16(at + 4); u.Unknown = S16(at + 6); }
        else numLocal = S32(at + 4);
        int numCall = S32(at + 8);
        int numSlot = S32(at + 0x14), numAction = S32(at + 0x18), numActionTrig = S32(at + 0x1C);
        int numProp = S32(at + 0x20), numPropTrig = S32(at + 0x24), numAlways = S32(at + 0x28);
        long trigTable = at + Ptr(wide ? at + 0x30 : at + 0x2C);
        long p = at + (wide ? 0x38 : 0x30);
        for (int i = 0; i < numLocal; i++) u.LocalProperties.Add(Name(Ptr(p + i * P)));
        p += numLocal * P;
        for (int i = 0; i < f.UserParams.Count; i++) u.Params.Add(new(f.UserParams[i].Name, Value(U32(p + i * 4), f.UserParams[i].Type)));
        p += f.UserParams.Count * 4 + numCall * 2;
        p = (p + 3) & ~3L;

        int cz = wide ? 0x30 : 0x20;
        long calls = p, containers = calls + numCall * cz;
        for (int i = 0; i < numCall; i++)
        {
            long c = calls + i * cz;
            long q = c + P;
            var a = new AssetCall
            {
                KeyName = Name(Ptr(c)), Flag = U16(q + 2), Duration = S32(q + 4),
                ParentIndex = S32(q + 8), Guid = U32(q + 0xC),
            };
            q = c + (wide ? 0x20 : 0x18);
            long ps = Ptr(q), cond = Ptr(q + P);
            if ((a.Flag & 1) == 0)
            {
                long pp = assetParamTable + ps;
                a.Params = MaskedParams(U64(pp), pp + 8, f.AssetParams);
            }
            else a.Container = ReadContainer(containers + ps);
            if (cond != None) a.Condition = ReadCondition(cond);
            u.AssetCalls.Add(a);
        }

        int slotSize = 2 * P, actionSize = wide ? 0x10 : 0xC, atSize = wide ? 0x28 : 0x18;
        int propSize = wide ? 0x18 : 0x10, ptSize = wide ? 0x20 : 0x14;
        long slots = trigTable, actions = slots + numSlot * slotSize, atr = actions + numAction * actionSize;
        long props = atr + numActionTrig * atSize, ptr = props + numProp * propSize, always = ptr + numPropTrig * ptSize;

        for (int i = 0; i < numSlot; i++)
        {
            long s = slots + i * slotSize;
            var slot = new ActionSlot { Name = Name(Ptr(s)) };
            int a0 = S16(s + P), a1 = S16(s + P + 2);
            for (int ai = a0; ai >= 0 && ai <= a1; ai++)
            {
                long ap = actions + ai * actionSize;
                var act = new Action { Name = Name(Ptr(ap)) };
                int t0, t1;
                if (wide) { t0 = S16(ap + 8); act.IsPrefix = d[ap + 0xA] != 0; t1 = S32(ap + 0xC); }
                else { t0 = S32(ap + 4); t1 = S32(ap + 8); }
                for (int ti = t0; ti >= 0 && ti <= t1; ti++) act.Triggers.Add(ReadActionTrigger(atr + ti * atSize, cz));
                slot.Actions.Add(act);
            }
            u.ActionSlots.Add(slot);
        }
        for (int i = 0; i < numProp; i++)
        {
            long pp = props + i * propSize;
            var pr = new Property { WatchPropertyName = Name(Ptr(pp)), IsGlobal = U32(pp + P) != 0 };
            int t0 = S32(pp + P + 4), t1 = S32(pp + P + 8);
            for (int ti = t0; ti >= 0 && ti <= t1; ti++)
            {
                long t = ptr + ti * ptSize;
                var tr = new Trigger { Guid = U32(t) };
                long ow;
                if (wide)
                {
                    tr.Flag = U16(t + 4); tr.OverwriteHash = U16(t + 6);
                    tr.AssetCallIndex = (int)(Ptr(t + 8) / cz);
                    tr.Condition = (SwitchCondition)ReadCondition(Ptr(t + 0x10));
                    ow = Ptr(t + 0x18);
                }
                else
                {
                    tr.AssetCallIndex = (int)(Ptr(t + 4) / cz);
                    tr.Condition = (SwitchCondition)ReadCondition(Ptr(t + 8));
                    tr.Flag = U16(t + 0xC); tr.OverwriteHash = U16(t + 0xE);
                    ow = Ptr(t + 0x10);
                }
                tr.Overwrite = Overwrite(ow);
                pr.Triggers.Add(tr);
            }
            u.Properties.Add(pr);
        }
        for (int i = 0; i < numAlways; i++)
        {
            long t = always + i * (wide ? 0x18 : 0x10);
            var tr = new Trigger { Guid = U32(t) };
            long ow;
            if (wide)
            {
                tr.Flag = U16(t + 4); tr.OverwriteHash = U16(t + 6);
                tr.AssetCallIndex = (int)(Ptr(t + 8) / cz);
                ow = Ptr(t + 0x10);
            }
            else
            {
                tr.AssetCallIndex = (int)(Ptr(t + 4) / cz);
                tr.Flag = U16(t + 8); tr.OverwriteHash = U16(t + 0xA);
                ow = Ptr(t + 0xC);
            }
            tr.Overwrite = Overwrite(ow);
            u.AlwaysTriggers.Add(tr);
        }
        return u;
    }

    Trigger ReadActionTrigger(long t, int cz)
    {
        var tr = new Trigger { Guid = U32(t) };
        long q = t + 4;
        if (wide) { tr.Unknown1 = S32(q); q += 4; }
        tr.AssetCallIndex = (int)(Ptr(q) / cz);
        q += P;
        tr.EndFrame = S32(q + P);
        tr.Flag = U16(q + P + 4);
        tr.OverwriteHash = U16(q + P + 6);
        if (tr.IsPrevious) tr.PreviousActionName = Name(Ptr(q));
        else tr.StartFrame = S32(q);
        tr.Overwrite = Overwrite(Ptr(q + P + 8));
        return tr;
    }
}
