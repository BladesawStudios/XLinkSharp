# XLinkSharp

A C# reader and writer for **XLNK**, the ELink and SLink databases (`.belnk`, `.bslnk`) that *Breath of the Wild* and
*Tears of the Kingdom* keep their effect and sound events in. It also reads and writes the text form of
[xlink2](https://github.com/dt-12345/xlink2), so a database can be edited as text without the tool.

```csharp
XLinkFile file = XLinkFile.FromBinary(bytes);           // or FromFile(path)
string text = file.ToText();                            // the xlink2 text form

XLinkFile edited = XLinkFile.FromText(text, XLinkGame.Totk);
byte[] rebuilt = edited.ToBinary();
```

`FromText` needs the game because the text only says whether the file is an ELink or an SLink. The binary layout
(32-bit for BotW, 64-bit for TotK) follows the game.

## The text form

The same text as `xlink -ot text` with its default braces:

```
Users {
  Demo {
    Unknown = 3
    UserParams {
      Radius = RANDOM {
        Type = InflectedPolynomial
        Power = 3.0
        Min = 0.5
        Max = 2.5
      }
    }
    Properties {
      Local::Speed {
        if <value> >= 2.5 => 0x00000020 {
          ...
    AssetCallTables {
      Main[0x00000001] {
        Execute = Switch (Local::Mode) {
          (<value> == Mode::Fast) => Loop[0x00000003] {
          ...
```

- Floats print as `1.0` when whole and with nine significant digits otherwise. Names are bare unless they hold
  whitespace or one of `" = ! < > ( ) { } [ ] @ , # :`, and then they are quoted and escaped.
- A call table is written once, where it sits in the tree. Everywhere else it is a reference, `Key[0xGUID]`: in
  triggers, grid cases and jump containers. Children of a container must have a body.
- Asset parameters that are not listed stay unset. User parameters that are not listed are filled in with their
  define's default.
- When reading, `#` starts a comment that runs to the end of the line.
- Errors are `FormatException`s that give the line.

## Limits

- Only the braces form is read. The tool's `--no-braces` indented form is not.
- Containers need at least one child, as in the tool's loader, and an unknown parameter name is an error rather than
  being dropped. An integer is accepted where a float is expected.
- A file whose call tables share a GUID prints those GUIDs as they are. The tool would draw new random ones.
- An action or property with no triggers is written with an empty range (end before start). The tool's own save
  writes `-1, 0` there, which its loader reads back as two triggers. No game file has one.
- Wii U (big-endian) BotW files are read and written by the binary code but have not been checked against the tool.

## Build

```bash
dotnet build XLinkSharp.sln -c Release
dotnet test XLinkSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md). The text form and its rules follow
[xlink2](https://github.com/dt-12345/xlink2) by dt-12345, which is GPL-3.0.
