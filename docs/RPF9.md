# RPF9 browsing and extraction

CodeWalker detects `9FPR` archives and can list, decrypt, preview and extract
their supported payloads. This implementation supports the investigated Xbox
executable build, identified by SHA-256:

```
e95020b2fa4a721db3df59fdecf75621730ac559d8835252f6c3b658d5a08993
```

The executable must be supplied by the user. No executable, archive, key blobs
or substitution tables are included in the repository. The executable is read
as data and never launched. Other executable hashes fail explicitly.

## Opening archives

The normal game picker now includes **GTA 6 (RPF9, read-only)** alongside
GTAV Legacy and Enhanced. On first launch, or through **File > Configure
Game...**, select GTA 6 and use the executable browse button to choose
`game_scarlett_master.exe`. Choosing that file also selects GTA 6 and its
containing folder. Select or enter your archives folder, then press **OK**.
The game and executable path are saved with the existing user settings.
Normal startup with GTA 6 selected opens the archive Explorer; it does not
attempt to load the GTAV world. No command-line argument is needed.

The executable is checked against the supported SHA-256 before the selection
is accepted. GTA 6 can use an executable outside the archives folder: choose
the executable first, then select or type the archives folder. GTAV editions
continue to use their corresponding executable inside the selected game folder.
Existing GTAV Legacy/Enhanced settings are preserved when no game choice has
yet been saved.

Build the Release configuration of `CodeWalker.RPFExplorer` using Visual
Studio 2022 with .NET desktop development, the .NET Framework 4.8 targeting
pack and a .NET SDK. From a Visual Studio Developer PowerShell:

```powershell
msbuild CodeWalker.RPFExplorer\CodeWalker.RPFExplorer.csproj /restore /p:Configuration=Release
cd CodeWalker.RPFExplorer\bin\Release\net48
& '.\CodeWalker RPF Explorer.exe' --rpf9 'D:\GameArchives'
```

Alternatively, `CodeWalker.exe --rpf9 D:\GameArchives` starts the same mode.
This bypasses GTAV folder selection, GTAV key loading and the GTAV asset cache.
An already open Explorer also has **File > Open RPF9 folder...**.

Place `game_scarlett_master.exe` in the archive folder or a parent folder.
To use an executable elsewhere, set `CODEWALKER_RPF9_EXE` to its full path
before launching. Core-library users can set `Rpf9KeyStore.ExecutablePath`.
The folder scan includes subdirectories; select a folder containing the
archives you want to browse rather than a large extraction-output tree.

Archives appear in the normal Explorer tree. Files currently have numeric
names such as `000000.bin`, because original names and hierarchy have not
been recovered. XML beginning with `<?xml` opens in the XML viewer; other
unknown bodies use the hex viewer. **Extract raw**, **Extract uncompressed**
and **Extract all** stream RPF9 outputs to disk. Resource entries export their
decoded bodies; no GTAV resource header is added.

Raw, unencrypted nested RPF9 containers are detected and scanned using byte
offsets. Other nested containers can be exported and opened separately if
their type is known. In-memory previews cannot exceed the .NET byte-array
limit; streamed extraction supports larger entries. Exports are staged and
validated before replacing the chosen output, and cannot overwrite their
own source archive.

RPF9 is read-only. Archive creation inside it, import, deletion, rename,
defragmentation and conversion to OPEN encryption are blocked in the core
API. RPF9 mode does not enable edit mode. This does not add model, texture,
map or script viewers for previously unsupported game resource layouts.

## Implemented format paths

- Executable key-bank and table extraction after exact SHA-256 verification.
- Outer CBC metadata transform and descriptor-1 CBC transforms for both the
  primary and extension regions, each beginning with the configuration IV.
- Typed logical-to-physical directory indexes, 24-byte binary records and
  20-byte resource records; optional cipher, block and metadata-hash records.
- Type-17 payload CTR with the native selective head/block/gap/tail ranges.
  Counter positions include clear gaps and use absolute payload byte offsets.
- Compact block formats 2, 3 and 4; stored and compressed blocks; 16-byte
  source alignment; zlib (ID 0) and raw DEFLATE (ID 1).
- Streaming reads, selective transforms and decompression, including large
  blocks. Zlib checksums, compressed-stream boundaries and decoded sizes are
  checked. Stored payloads and raw DEFLATE have no compression checksum.

Unknown cipher/compression/block layouts and optional-extension storage for
the decoded record types fail explicitly. Metadata allocation is limited to
256 MiB. Automatic nested scanning has a depth limit of 16.

`MetadataHash` is retained as an uninterpreted value; it is not presented as
a recovered name or a verified content digest. The legacy 32-bit display
fields on `RpfBinaryFileEntry` saturate for huge files; `Record.StoredSize`,
`Record.DecodedSize`, `Record.Offset` and `GetFileSize()` retain full sizes.

## Validation

Run the portable synthetic tests and an RPF7 create/scan/extract regression:

```powershell
dotnet run --project CodeWalker.Rpf9.Tests
```

An optional private-fixture run uses the earlier Python research manifests
to compare output hashes. No game files or manifests need to be committed:

```powershell
dotnet run --project CodeWalker.Rpf9.Tests -- 'D:\ResearchFolder'
```

The recorded run passed **1,553 assertions**, including **1,527 payload
SHA-256 comparisons** against the independent Python extractor: all 418
common entries, both script subarchives (2 and 1,012 entries), all 3 shader
entries, appdata, and 90 selected entries across all 22 original archives.
It includes the 275,540,780-byte shader output and nested physical-offset
extraction. These checks do not establish full extraction of every large
archive in the collection. Format 4 is covered by a synthetic descriptor
test; it was not present in the inspected collection.

The Windows-only configuration checks exercise all three game choices,
executable matching, unsupported GTA 6 build rejection and migration of old
GTAV settings. They run in CI without game files and never save user profiles:

```powershell
msbuild CodeWalker.Rpf9.DesktopTests\CodeWalker.Rpf9.DesktopTests.csproj /restore /p:Configuration=Release
& '.\CodeWalker.Rpf9.DesktopTests\bin\Release\net48\CodeWalker.Rpf9.DesktopTests.exe' --configuration
```

The optional private-fixture smoke test chooses the verified GTA 6 executable
through the selector and starts the default Explorer constructor, exercising
configured-game startup without `--rpf9`. It runs a hidden Explorer, waits for its scan,
navigates common.rpf, checks all 418 list items and their virtual paths,
exports decrypted XML through the UI save helper, and rejects overwriting
the source archive. Use a test folder containing only common.rpf and the
supported executable:

```powershell
msbuild CodeWalker.Rpf9.DesktopTests\CodeWalker.Rpf9.DesktopTests.csproj /restore /p:Configuration=Release
& '.\CodeWalker.Rpf9.DesktopTests\bin\Release\net48\CodeWalker.Rpf9.DesktopTests.exe' 'D:\Rpf9SmokeTest'
```

The smoke test removes its temporary export. Release builds of the main
application, RPF Explorer and the desktop smoke test were validated with
Visual Studio MSBuild. `dotnet build` may require additional resource-task
configuration for this repository's existing .NET Framework WinForms assets;
use Visual Studio MSBuild for the desktop projects.

## Evidence anchors

The port follows static instruction decoding of the verified executable:
metadata header reader `0x141fb2600`, block transforms `0x145642c50` and
`0x1456437c0`, CBC wrapper `0x1455dbcf0`, CTR transform `0x1455dc010`, selective
range routine `0x141616150` and compact block decoder `0x14550db40`.
Ghidra auto-analysis did not finish through the REA bridge; the working
translation was established with bounded offline instruction decoding and
then checked against real codec checksums, declared lengths and independent
extraction hashes. It does not depend on a Ghidra installation at runtime.
