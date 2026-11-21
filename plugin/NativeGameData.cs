using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NativeCompatibility;
using CounterStrikeSharp.API.Modules.Memory;

namespace BotNative;

// One external file, loaded once per plugin lifetime. No filesystem work on ticks.
internal sealed class NativeGameData
{
    private static readonly Lazy<NativeGameData> Current = new(() =>
        Load(FindPath(), (type, field) => Schema.GetSchemaOffset(type, field)));
    // These consumers use CSS for entity fields. Unrelated bot Schema changes
    // must not prevent binding a verified purchase, grenade or smoke function.
    private static readonly Lazy<NativeGameData> FunctionProfile = new(() => Load(FindPath()));
    private static string? pluginPath;
    // CSS loads plugin assemblies from memory, so Assembly.Location can be empty.
    internal static void Configure(string modulePath) => pluginPath = Path.GetFullPath(modulePath);
    internal static NativeGameData Instance => Current.Value;
    internal sealed record Entry(int Rva, byte[] Expected);
    private Dictionary<string, Entry> entries;
    private readonly Dictionary<string, JsonElement> relocations = new();
    private Dictionary<string, int> offsets;
    private readonly Dictionary<string, string> signatures;
    internal string ServerSha256 { get; }
    internal string Build { get; }
    private readonly string? spreadTier0Sha256;
    private readonly Dictionary<string, string> imageFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (nint Address, int Size)> verifiedModules = new(StringComparer.OrdinalIgnoreCase);

    private NativeGameData(JsonElement root)
    {
        if (root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported bot-native.json format");
        Build = root.GetProperty("build").GetString()!;
        ServerSha256 = Hash(root, "serverSha256");
        spreadTier0Sha256 = root.TryGetProperty("spreadTier0Sha256", out _) ? Hash(root, "spreadTier0Sha256") : null;
        if (root.TryGetProperty("moduleFingerprints", out var profiles))
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.GetProperty("algorithm").GetString() != PeImageFingerprint.Algorithm)
                    throw new InvalidDataException($"Unsupported fingerprint algorithm: {profile.Name}");
                imageFingerprints.Add(profile.Name, Hash(profile.Value, "sha256"));
            }
        entries = root.GetProperty("entries").EnumerateObject().ToDictionary(p => p.Name, p =>
        {
            int rva = Number(p.Value.GetProperty("rva"));
            byte[] expected = Convert.FromHexString(p.Value.GetProperty("expected").GetString()!.Replace(" ", ""));
            if (rva <= 0 || expected.Length < 5) throw new InvalidDataException($"Invalid native entry {p.Name}");
            return new Entry(rva, expected);
        });
        offsets = root.GetProperty("offsets").EnumerateObject().ToDictionary(p => p.Name, p => Number(p.Value));
        signatures = root.GetProperty("signatures").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        if (root.TryGetProperty("relocations", out var recipes))
            foreach (var recipe in recipes.EnumerateObject())
                relocations.Add(recipe.Name, recipe.Value.Clone());
    }

    private static string Hash(JsonElement root, string name)
    {
        string value = root.GetProperty(name).GetString()!;
        if (Convert.FromHexString(value).Length != 32) throw new InvalidDataException($"Invalid {name}");
        return value.ToUpperInvariant();
    }
    private static int Number(JsonElement value)
    {
        string s = value.GetString()!;
        int n = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : int.Parse(s, CultureInfo.InvariantCulture);
        return n >= 0 ? n : throw new InvalidDataException("Negative native offset");
    }
    // Values in JSON remain the audited offline snapshot. At runtime these named
    // fields must come from Schema; private layouts still require the PE profile.
    private static readonly (string Key, string Type, string Field)[] SchemaFields =
    [
        ("botPawn", "CBot", "m_pPlayer"),
        ("hearingPosition", "CCSBot", "m_noisePosition"),
        ("hearingTime", "CCSBot", "m_noiseTimestamp"),
        ("hearingSource", "CCSBot", "m_noiseSource"),
        ("enemyVisible", "CCSBot", "m_isEnemyVisible"),
        ("aimingEnemy", "CCSBot", "m_isAimingAtEnemy"),
        ("targetSpot", "CCSBot", "m_targetSpot"),
        ("enemyHandle", "CCSBot", "m_enemy"),
        ("lastEnemyPosition", "CCSBot", "m_lastEnemyPosition"),
        ("aimError", "CCSBot", "m_aimError"),
        ("aimFocus", "CCSBot", "m_aimFocus"),
        ("queueIndex", "CCSBot", "m_enemyQueueIndex"),
        ("queueCount", "CCSBot", "m_enemyQueueCount"),
        ("queueAttend", "CCSBot", "m_enemyQueueAttendIndex")
    ];
    internal static NativeGameData Load(string path, Func<string, string, int>? schemaOffset = null)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var data = new NativeGameData(json.RootElement);
        if (schemaOffset != null)
            foreach (var (key, type, field) in SchemaFields)
            {
                int offset = schemaOffset(type, field);
                // All selected fields are nonzero. CSS reports missing fields as
                // zero on some releases; never silently use the JSON value then.
                if (offset <= 0) throw new InvalidDataException($"Schema field unavailable: {type}::{field}");
                data.offsets[key] = offset;
            }
        return data;
    }
    private static string FindPath() => ResolvePath(pluginPath ?? typeof(NativeGameData).Assembly.Location);
    internal static string ResolvePath(string modulePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(modulePath)
            ?? throw new InvalidOperationException("Configure NativeGameData with the CSS ModulePath before binding"));
        return Path.Combine(directory.FullName, "bot-native.json");
    }
    // CSS 1.0.380 keys a registration by module, signature and native prototype.
    internal static string CssSignatureKey(string binary, string signature, DataType result, params DataType[] arguments)
        => $"{binary}:{signature}:{result}:{string.Join(",", arguments.AsEnumerable())}";

    internal Entry Function(string name) => entries.TryGetValue(name, out var value) ? value
        : throw new InvalidDataException($"bot-native.json missing entry: {name}");
    internal int Offset(string name) => offsets.TryGetValue(name, out int value) ? value
        : throw new InvalidDataException($"bot-native.json missing offset: {name}");
    internal string Signature(string name) => signatures.TryGetValue(name, out string? value) ? value
        : throw new InvalidDataException($"bot-native.json missing signature: {name}");
    internal string EntrySignature(string name) => string.Join(" ", Function(name).Expected.Select(b => b.ToString("X2")));

    internal (nint Address, int Size) Server() => Module("server.dll", ServerSha256, true);
    internal (nint Address, int Size) SpreadTier0() => Module("tier0.dll", spreadTier0Sha256
        ?? throw new InvalidDataException("bot-native.json missing spreadTier0Sha256"), false);
    internal static (NativeGameData Data, (nint Address, int Size) Module) ServerFunctions(params string[] names)
    {
        var profile = FunctionProfile.Value;
        var module = FindModule("server.dll", true);
        var file = File.ReadAllBytes(module.Path);
        if (profile.MatchesModule("server.dll", file, profile.ServerSha256, out _))
            return (profile, (module.Address, module.Size));
        var scope = profile.RelocateFunctions(file, names);
        Console.WriteLine($"[bot-native] Relocated on {Path.GetFileName(module.Path)}: {string.Join(", ", names)}");
        return (scope, (module.Address, module.Size));
    }

    // A scoped result never marks the module verified for raw offsets or patches.
    internal NativeGameData RelocateFunctions(byte[] file, params string[] names)
    {
        using var reader = new PEReader(new MemoryStream(file, writable: false));
        var headers = reader.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus)
            throw new InvalidDataException("Native relocation requires Windows x64");
        var sections = headers.SectionHeaders.Where(s => s.VirtualSize > 0 && s.SizeOfRawData > 0
            && (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)
            .ToArray();
        var result = (NativeGameData)MemberwiseClone();
        result.entries = new();
        result.offsets = new();
        foreach (string name in names)
        {
            if (!relocations.TryGetValue(name, out var recipe))
                throw new InvalidDataException($"No independent relocation proof for {name}");
            var locator = new FunctionLocator(file, sections, name);
            result.entries.Add(name, locator.Resolve(Function(name).Expected, recipe, signatures.GetValueOrDefault(name)));
            string span = name + "HookBytes";
            if (offsets.TryGetValue(span, out int length)) result.offsets.Add(span, length);
        }
        return result;
    }

    private sealed class FunctionLocator(byte[] file, SectionHeader[] sections, string name)
    {
        internal Entry Resolve(byte[] expected, JsonElement recipe, string? css)
        {
            int rva;
            if (recipe.TryGetProperty("caller", out var caller))
            {
                string[] pattern = caller.GetString()!.Split(' ');
                int call = Array.IndexOf(pattern, "?") - 1;
                if (call < 0 || pattern[call] != "E8" || !pattern.Skip(call + 1).Take(4).SequenceEqual(new[] { "?", "?", "?", "?" }))
                    throw new InvalidDataException($"Invalid relative call locator for {name}");
                int site = Unique(pattern);
                rva = RelativeTarget(checked(site + call), 0xE8);
            }
            else
            {
                var rel = recipe.GetProperty("rel32");
                int stablePrefix = rel.GetArrayLength() == 0 ? expected.Length : Math.Min(expected.Length, rel[0].GetInt32());
                if (stablePrefix < 5) throw new InvalidDataException($"Insufficient anchor for {name}");
                rva = Unique(expected.Take(stablePrefix).Select(b => b.ToString("X2")).ToArray());
            }
            VerifyBody(rva, recipe);
            if (recipe.TryGetProperty("tailTarget", out var tail))
                VerifyBody(RelativeTarget(checked(rva + recipe.GetProperty("rel32")[0].GetInt32() - 1), 0xE9), tail);
            byte[] diskPrefix = ReadCode(rva, expected.Length).ToArray();
            // CSS does its own signature lookup. Prove it selects this entry too.
            if (css != null && Unique(css.Split(' ')) != rva)
                throw new InvalidDataException($"CSS signature differs from relocated {name}");
            return new Entry(rva, diskPrefix);
        }

        private void VerifyBody(int start, JsonElement body)
        {
            byte[] code = ReadCode(start, body.GetProperty("length").GetInt32()).ToArray();
            int end = 0;
            foreach (var item in body.GetProperty("rel32").EnumerateArray())
            {
                int offset = item.GetInt32();
                if (offset < end || offset > code.Length - 4) throw new InvalidDataException($"Invalid rel32 map for {name}");
                code.AsSpan(offset, 4).Clear();
                end = offset + 4;
            }
            if (Convert.ToHexString(SHA256.HashData(code)) != Hash(body, "sha256"))
                throw new InvalidDataException($"Native function body changed for {name} at RVA 0x{start:X}");
        }

        private int RelativeTarget(int at, byte opcode)
        {
            var instruction = ReadCode(at, 5);
            if (instruction[0] != opcode) throw new InvalidDataException($"Invalid branch for {name}");
            return checked(at + 5 + BinaryPrimitives.ReadInt32LittleEndian(instruction[1..]));
        }

        private int Unique(string[] tokens)
        {
            byte?[] pattern = tokens.Select(t => t == "?" ? (byte?)null : Convert.ToByte(t, 16)).ToArray();
            byte[] prefix = pattern.TakeWhile(b => b.HasValue).Select(b => b!.Value).ToArray();
            if (prefix.Length == 0) throw new InvalidDataException($"Empty locator prefix for {name}");
            int found = -1;
            foreach (var section in sections)
            {
                var bytes = ReadCode(section.VirtualAddress, Math.Min(section.SizeOfRawData, section.VirtualSize));
                for (int pos = 0; pos <= bytes.Length - pattern.Length; pos++)
                {
                    int next = bytes[pos..].IndexOf(prefix);
                    if (next < 0) break;
                    pos += next;
                    if (pos > bytes.Length - pattern.Length) break;
                    int i = prefix.Length;
                    while (i < pattern.Length && (pattern[i] == null || pattern[i] == bytes[pos + i])) i++;
                    if (i != pattern.Length) continue;
                    if (found >= 0) throw new InvalidDataException($"Ambiguous native locator for {name}");
                    found = checked(section.VirtualAddress + pos);
                }
            }
            return found >= 0 ? found : throw new InvalidDataException($"No native locator match for {name}");
        }

        private ReadOnlySpan<byte> ReadCode(int rva, int length)
        {
            if (length > 0)
                foreach (var s in sections)
                {
                    long pos = (long)rva - s.VirtualAddress;
                    if (pos >= 0 && pos + length <= Math.Min(s.VirtualSize, s.SizeOfRawData))
                        return file.AsSpan(checked(s.PointerToRawData + (int)pos), length);
                }
            throw new InvalidDataException($"RVA 0x{rva:X}+{length} outside executable code");
        }
    }

    private (nint Address, int Size) Module(string name, string hash, bool server)
    {
        // Engine modules live for the process lifetime. Cache only successful
        // validation per plugin instance; never hash once per bot or per tick.
        lock (verifiedModules)
        {
            if (verifiedModules.TryGetValue(name, out var cached)) return cached;
            var found = FindModule(name, server);
            VerifyModuleFile(name, File.ReadAllBytes(found.Path), hash);
            return verifiedModules[name] = (found.Address, found.Size);
        }
    }
    private static (nint Address, int Size, string Path) FindModule(string name, bool server)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("bot-native.json requires Windows x64");
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            if (!module.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (server && !module.FileName.Replace('\\', '/').EndsWith("/csgo/bin/win64/server.dll", StringComparison.OrdinalIgnoreCase)) continue;
            return (module.BaseAddress, module.ModuleMemorySize, module.FileName);
        }
        throw new InvalidOperationException($"Native module unavailable: {name}");
    }
    private bool MatchesModule(string name, byte[] file, string legacyHash, out string actual)
    {
        bool semantic = imageFingerprints.TryGetValue(name, out string? expected);
        actual = semantic ? PeImageFingerprint.Compute(file) : Convert.ToHexString(SHA256.HashData(file));
        return actual == (semantic ? expected : legacyHash);
    }
    internal void VerifyModuleFile(string name, byte[] file, string legacyHash)
    {
        if (!MatchesModule(name, file, legacyHash, out string actual))
            throw new InvalidOperationException($"{name} fingerprint differs from bot-native.json ({Build}); actual={actual}; audit required");
    }
    internal nint Address((nint Address, int Size) module, string name)
    {
        var entry = Function(name);
        if (entry.Rva > module.Size - entry.Expected.Length) throw new InvalidDataException($"Native entry outside module: {name}");
        return module.Address + entry.Rva;
    }
    internal nint Verify((nint Address, int Size) module, string name, int skip = 0)
    {
        var entry = Function(name);
        if (skip < 0 || skip >= entry.Expected.Length) throw new ArgumentOutOfRangeException(nameof(skip));
        nint address = Address(module, name);
        byte[] actual = new byte[entry.Expected.Length - skip];
        Marshal.Copy(address + skip, actual, 0, actual.Length);
        if (!actual.AsSpan().SequenceEqual(entry.Expected.AsSpan(skip)))
            throw new InvalidOperationException($"Native entry {name} changed or already hooked (bot-native.json, RVA 0x{entry.Rva:X})");
        return address;
    }
    internal T Bind<T>((nint Address, int Size) module, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Verify(module, name));

    // Call only for a registered CSS hook or the verified shared hook runtime.
    // KHook displaces whole instructions, then pads the rel32 jump with NOPs.
    internal nint VerifyDetour((nint Address, int Size) module, string name)
    {
        int span = Offset(name + "HookBytes");
        if (span < 5) throw new InvalidDataException($"Invalid {name} hook span");
        nint address = Verify(module, name, span);
        if (Marshal.ReadByte(address) != 0xE9)
            throw new InvalidOperationException($"Native entry {name} has no supported detour");
        for (int i = 5; i < span; i++)
            if (Marshal.ReadByte(address, i) != 0x90)
                throw new InvalidOperationException($"Unrecognized {name} hook padding");
        return address;
    }
}
