<#
Over The Hill mods: installer.

Double-click Install.bat (it starts this script). What it does:
  1. Finds the game (Steam), and checks that the game is not running.
  2. Sets up BepInEx 6 IL2CPP build 788 if the game does not have it (see "Why BepInEx is downloaded" below).
  3. Shows a menu: everything, quality of life only, choose mods, update, uninstall.
  4. Copies the mod DLLs into BepInEx\plugins\OverTheHillMods and writes BepInEx\config\oth.core.enabled.txt.

It needs no admin rights and never touches anything outside the game folder, except a temporary folder for the
BepInEx download. Plain text: read it before you run it.

Why BepInEx is downloaded and not shipped in this zip: BepInEx is someone else's work (LGPL 2.1). Redistributing it
would be allowed, but a download from its official build server is always the real thing, keeps this zip small and
leaves its license and updates with its authors. The download is checked against a fixed SHA-256 and refused if it
differs.

Options for testing or scripting (not needed for normal use):
  -GameDir <folder>        use this game folder, do not look for it
  -NoBepInExDownload       do not install BepInEx (a game folder that already has it, or a test folder)
  -Action <name>           everything | qol | choose | update | uninstall, no menu
  -Mods <ids>              with -Action choose: the mod ids to install, e.g. -Mods chat,graphics
  -Offline                 update from the files in this download, do not look online
  -Yes                     answer every question with its default, never wait for a key
  -RemoveBepInEx           with -Action uninstall: also remove BepInEx
  -RemoveSettings          with -Action uninstall: also remove the mods' settings files
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [switch]$NoBepInExDownload,
    [ValidateSet("", "everything", "qol", "choose", "update", "uninstall")][string]$Action = "",
    [string[]]$Mods = @(),
    [switch]$Offline,
    [switch]$Yes,
    [switch]$RemoveBepInEx,
    [switch]$RemoveSettings
)

$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---- fixed facts -------------------------------------------------------------------------------------------------
$AppId = "2929250"
$GameExe = "over the hill.exe"
$GameProcess = "over the hill"
$RepoPrefix = "https://github.com/legomaster188/OverTheHillPlus-QoL/"
$ManifestUrl = $RepoPrefix + "releases/latest/download/mods.json"
$BepName = "BepInEx 6.0.0-be.788 (IL2CPP, 64 bit)"
$BepUrl = "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip"
$BepZipSha256 = "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a"
# SHA-256 of BepInEx\core\BepInEx.Unity.IL2CPP.dll inside that zip: how an installed copy is recognised.
$BepCoreSha256 = "46cf1ef802bdf8cb6587fc1e4d98f7af1073a60487b39a64e230e6f6e4c23aec"
$Categories = @("qol", "multiplayer", "cars", "tools")
$CategoryTitles = @{
    "qol"         = "Quality of life"
    "multiplayer" = "Multiplayer and servers"
    "cars"        = "New cars and car parts"
    "tools"       = "Host and solo tools (cheats)"
}

# ---- small helpers -----------------------------------------------------------------------------------------------
function Say([string]$text, [string]$color = "") {
    if ($color) { Write-Host $text -ForegroundColor $color } else { Write-Host $text }
}
function Good([string]$text) { Say ("  OK    " + $text) "Green" }
function Warn([string]$text) { Say ("  NOTE  " + $text) "Yellow" }
function Fail([string]$text) { throw $text }

function Ask([string]$question, [string]$default = "") {
    if ($Yes) { return $default }
    $suffix = ""
    if ($default) { $suffix = " [" + $default + "]" }
    $answer = Read-Host ($question + $suffix)
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

function AskYesNo([string]$question, [bool]$default) {
    $d = "n"
    if ($default) { $d = "y" }
    $a = Ask ($question + " (y/n)") $d
    return $a.ToLower().StartsWith("y")
}

# Shows non-ASCII letters without their accents, so any console code page prints them.
function AsciiText([string]$text) {
    if (-not $text) { return "" }
    $d = $text.Normalize([Text.NormalizationForm]::FormD)
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $d.ToCharArray()) {
        $cat = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($c)
        if ($cat -eq [Globalization.UnicodeCategory]::NonSpacingMark) { continue }
        if ([int]$c -lt 128) { [void]$sb.Append($c) } else { [void]$sb.Append("?") }
    }
    return $sb.ToString()
}

function ShortText([string]$text) {
    if (-not $text) { return "" }
    $t = (AsciiText $text).Trim()
    $i = $t.IndexOf(". ")
    if ($i -gt 20) { $t = $t.Substring(0, $i) }
    $t = $t.TrimEnd(".")
    if ($t.Length -gt 76) { $t = $t.Substring(0, 73).TrimEnd() + "..." }
    return $t
}

function Sha256Of([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
}

function Sha256OfBytes([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace "-", "").ToLower() } finally { $sha.Dispose() }
}

function Utf8NoBom { return New-Object System.Text.UTF8Encoding($false) }

# ---- finding the game --------------------------------------------------------------------------------------------
function ResolveGameDir([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    $p = $path.Trim().Trim('"').Trim()
    if ($p.ToLower().EndsWith(".exe")) { $p = Split-Path -Parent $p }
    if ($p -and (Test-Path -LiteralPath (Join-Path $p $GameExe))) { return (Resolve-Path -LiteralPath $p).ProviderPath.TrimEnd("\") }
    return $null
}

function SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $v = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @("SteamPath", "InstallPath")) {
                if ($v.$name) { $roots.Add(([string]$v.$name) -replace "/", "\") }
            }
        } catch { }
    }
    $roots.Add(${env:ProgramFiles(x86)} + "\Steam")
    $roots.Add($env:ProgramFiles + "\Steam")
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($r in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $r)) { continue }
        $libs.Add($r)
        $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            $text = [IO.File]::ReadAllText($vdf)
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $libs.Add(($m.Groups[1].Value -replace "\\\\", "\"))
            }
        }
    }
    return ($libs | Select-Object -Unique)
}

function FindGameDir {
    foreach ($lib in (SteamLibraries)) {
        $steamapps = Join-Path $lib "steamapps"
        $acf = Join-Path $steamapps ("appmanifest_" + $AppId + ".acf")
        $names = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath $acf) {
            $m = [regex]::Match([IO.File]::ReadAllText($acf), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $names.Add($m.Groups[1].Value) }
        }
        $names.Add("over the hill")
        foreach ($n in $names) {
            $dir = ResolveGameDir (Join-Path $steamapps ("common\" + $n))
            if ($dir) { return $dir }
        }
    }
    return $null
}

function GetGameDir {
    if ($GameDir) {
        $dir = ResolveGameDir $GameDir
        if (-not $dir) { Fail ("There is no '" + $GameExe + "' in " + $GameDir + ". Check the folder and try again.") }
        return $dir
    }
    $dir = FindGameDir
    if ($dir) { Good ("Found the game: " + $dir); return $dir }
    Say ""
    Say "I could not find the game on this PC."
    Say "In Steam: right click the game, Manage, Browse local files. Copy that folder's address and paste it here."
    if ($Yes) { Fail "No game folder found. Run again with -GameDir <folder>." }
    for ($i = 0; $i -lt 5; $i++) {
        $answer = Read-Host "Game folder (or Enter to quit)"
        if ([string]::IsNullOrWhiteSpace($answer)) { Fail "No game folder given." }
        $dir = ResolveGameDir $answer
        if ($dir) { Good ("Using " + $dir); return $dir }
        Warn ("There is no '" + $GameExe + "' in that folder. Try again.")
    }
    Fail "No game folder found."
}

function AssertGameClosed {
    while (Get-Process -Name $GameProcess -ErrorAction SilentlyContinue) {
        Warn "The game is running. Close it first (this installer never closes it for you)."
        if ($Yes) { Fail "The game is running. Close it and run the installer again." }
        [void](Read-Host "Press Enter when the game is closed")
    }
}

# ---- downloads ---------------------------------------------------------------------------------------------------
function Download([string]$url, [string]$dest) {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
    $old = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
    try { Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -TimeoutSec 900 } finally { $ProgressPreference = $old }
}

function DownloadBytes([string]$url) {
    $tmp = [IO.Path]::GetTempFileName()
    try {
        Download $url $tmp
        return [IO.File]::ReadAllBytes($tmp)
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

# ---- BepInEx -----------------------------------------------------------------------------------------------------
function EnsureBepInEx([string]$game) {
    $core = Join-Path $game "BepInEx\core\BepInEx.Unity.IL2CPP.dll"
    if (Test-Path -LiteralPath $core) {
        if ((Sha256Of $core) -eq $BepCoreSha256) { Good "BepInEx is already installed (build 788)."; return }
        Warn "BepInEx is installed, but not the build 788 these mods are made for."
        if (-not (AskYesNo "Replace it with build 788? (recommended; your other plugins stay where they are)" $false)) {
            Warn "Keeping your BepInEx. If the mods do not load, run this again and say yes."
            return
        }
    }
    if ($NoBepInExDownload) { Warn "Not installing BepInEx (-NoBepInExDownload)."; return }

    Say ""
    Say ("Downloading " + $BepName + " from builds.bepinex.dev (about 33 MB)...")
    $work = Join-Path ([IO.Path]::GetTempPath()) ("OverTheHillMods-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        $zip = Join-Path $work "BepInEx.zip"
        try { Download $BepUrl $zip } catch {
            Fail ("The BepInEx download failed (" + $_.Exception.Message + "). Check your internet connection, or install BepInEx by hand: see the README.")
        }
        if ((Sha256Of $zip) -ne $BepZipSha256) {
            Fail "The BepInEx download does not match the expected SHA-256, so it was not used. Try again later, or install BepInEx by hand: see the README."
        }
        Good "The download matches the expected SHA-256."
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $out = Join-Path $work "unzipped"
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $out)
        foreach ($item in (Get-ChildItem -LiteralPath $out -Force)) {
            $target = Join-Path $game $item.Name
            if ($item.Name -eq "doorstop_config.ini" -and (Test-Path -LiteralPath $target)) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $game "BepInEx\plugins") | Out-Null
        Good "BepInEx is installed."
    } finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---- the mod list ------------------------------------------------------------------------------------------------
function IsModFile([string]$file) { return ($file -cmatch '^[Oo][Tt][Hh]\.[A-Za-z0-9_-][A-Za-z0-9._-]*\.dll$') -and -not $file.Contains("..") }

# Reads a mods.json (schema 1) with the same checks as the mod manager in the game: nothing from a bad list is used.
function ReadManifest([string]$json) {
    $doc = $json.TrimStart([char]0xFEFF) | ConvertFrom-Json
    if ($doc.schema -ne 1) { Fail "The mod list has an unknown format." }
    $list = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($m in @($doc.mods)) {
        foreach ($k in @("id", "name", "version", "file", "url", "sha256")) {
            if ([string]::IsNullOrWhiteSpace([string]$m.$k)) { Fail ("A mod in the list has no " + $k + ".") }
        }
        if (-not (IsModFile $m.file)) { Fail ("The mod list names a file that is not a mod: " + $m.file) }
        if ($m.sha256 -cnotmatch '^[0-9a-f]{64}$') { Fail ("Bad SHA-256 for " + $m.id) }
        if (-not ([string]$m.url).StartsWith($RepoPrefix) -or ([string]$m.url).Contains("..") -or ([string]$m.url).Contains("%") -or ([string]$m.url).Contains("\") -or ([string]$m.url).Contains("@")) {
            Fail ("The download address for " + $m.id + " is not from this project's releases.")
        }
        if ($seen.ContainsKey($m.id.ToLower())) { Fail ("Mod " + $m.id + " is listed twice.") }
        $seen[$m.id.ToLower()] = $true
        $category = [string]$m.category
        if (-not $category) { $category = "qol" }
        $list.Add([pscustomobject]@{
            Id = [string]$m.id; Name = [string]$m.name; Version = [string]$m.version; File = [string]$m.file
            Url = [string]$m.url; Sha256 = [string]$m.sha256; Size = [int64]$m.size
            Description = [string]$m.description; Category = $category
        })
    }
    return $list
}

function ReadBundledManifest {
    $path = Join-Path $Here "mods.json"
    if (-not (Test-Path -LiteralPath $path)) {
        Fail "mods.json is missing next to Install.ps1. Unzip the whole download first, then run Install.bat."
    }
    return ReadManifest ([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8))
}

function ParseVersion([string]$v) {
    $t = $v.Trim().TrimStart("v")
    $cut = $t.IndexOfAny([char[]]@("-", "+", " "))
    if ($cut -ge 0) { $t = $t.Substring(0, $cut) }
    if (-not $t.Contains(".")) { $t = $t + ".0" }
    $out = $null
    if ([version]::TryParse($t, [ref]$out)) { return $out }
    return [version]"0.0"
}

# Bytes of a mod DLL for an entry: the file in this download if it matches, else downloaded and checked.
function GetModBytes($entry) {
    $local = Join-Path (Join-Path $Here "mods") $entry.File
    if (Test-Path -LiteralPath $local) {
        $bytes = [IO.File]::ReadAllBytes($local)
        if ((Sha256OfBytes $bytes) -eq $entry.Sha256) { return $bytes }
    }
    if ($Offline) { Fail ("The file for " + $entry.Name + " in this download is missing or damaged.") }
    $bytes = DownloadBytes $entry.Url
    if ($bytes.Length -ne $entry.Size -or (Sha256OfBytes $bytes) -ne $entry.Sha256) {
        Fail ("The download of " + $entry.Name + " does not match its SHA-256 or size, so it was not used.")
    }
    return $bytes
}

function PluginDir([string]$game) { return Join-Path $game "BepInEx\plugins\OverTheHillMods" }
function EnabledFile([string]$game) { return Join-Path $game "BepInEx\config\oth.core.enabled.txt" }

function PutMod([string]$game, $entry) {
    $dir = PluginDir $game
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir $entry.File
    $bytes = GetModBytes $entry
    $tmp = $target + ".new"
    [IO.File]::WriteAllBytes($tmp, $bytes)
    Move-Item -LiteralPath $tmp -Destination $target -Force
    try { Unblock-File -LiteralPath $target } catch { }
}

# Sets "id=true" / "id=false" in oth.core.enabled.txt, keeping every other line (the mod manager writes the same file).
function SetEnabled([string]$game, [string[]]$on, [string[]]$off) {
    $path = EnabledFile $game
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $path) {
        foreach ($l in [IO.File]::ReadAllLines($path)) { $lines.Add($l) }
    }
    $wanted = @{}
    foreach ($id in $on) { $wanted[$id.ToLower()] = "true" }
    foreach ($id in $off) { $wanted[$id.ToLower()] = "false" }
    $done = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $eq = $lines[$i].IndexOf("=")
        if ($eq -le 0 -or $lines[$i].TrimStart().StartsWith("#")) { continue }
        $key = $lines[$i].Substring(0, $eq).Trim().ToLower()
        if ($wanted.ContainsKey($key)) { $lines[$i] = $lines[$i].Substring(0, $eq).Trim() + "=" + $wanted[$key]; $done[$key] = $true }
    }
    foreach ($key in ($wanted.Keys | Sort-Object)) {
        if (-not $done.ContainsKey($key)) { $lines.Add($key + "=" + $wanted[$key]) }
    }
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), (Utf8NoBom))
}

function InstalledFiles([string]$game) {
    $dir = PluginDir $game
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter "*.dll" -File | ForEach-Object { $_.Name.ToLower() })
}

# ---- actions -----------------------------------------------------------------------------------------------------
function InstallMods([string]$game, $manifest, [string[]]$ids) {
    $core = $manifest | Where-Object { $_.Id -eq "core" } | Select-Object -First 1
    if (-not $core) { Fail "The mod list has no Mod Manager (core)." }
    $chosen = @($manifest | Where-Object { $ids -contains $_.Id -and $_.Id -ne "core" })
    $unknown = @($ids | Where-Object { $id = $_; -not ($manifest | Where-Object { $_.Id -eq $id }) })
    if ($unknown.Count -gt 0) { Fail ("Unknown mod id(s): " + ($unknown -join ", ")) }

    Say ""
    Say "Installing..."
    PutMod $game $core
    Good "Mod Manager (core)"
    foreach ($e in $chosen) { PutMod $game $e; Good (AsciiText $e.Name) }

    $installed = InstalledFiles $game
    $off = @($manifest | Where-Object { $_.Id -ne "core" -and ($ids -notcontains $_.Id) -and ($installed -contains $_.File.ToLower()) } | ForEach-Object { $_.Id })
    SetEnabled $game @($chosen | ForEach-Object { $_.Id }) $off
    Good "Wrote BepInEx\config\oth.core.enabled.txt"
    if ($off.Count -gt 0) {
        Warn ("Already installed but switched off now: " + (($off) -join ", ") + ". Switch them on in the game's MODS page, or run this again.")
    }
    Say ""
    Say ("Installed " + ($chosen.Count + 1) + " files into " + (PluginDir $game)) "Green"
}

function PickMods($manifest, [string]$game) {
    $installed = InstalledFiles $game
    $items = New-Object System.Collections.Generic.List[object]
    foreach ($c in $Categories) {
        foreach ($m in ($manifest | Where-Object { $_.Category -eq $c -and $_.Id -ne "core" } | Sort-Object Name)) { $items.Add($m) }
    }
    $others = @($manifest | Where-Object { $_.Id -ne "core" -and ($Categories -notcontains $_.Category) })
    foreach ($m in $others) { $items.Add($m) }
    $on = @{}
    if (@($items | Where-Object { $installed -contains $_.File.ToLower() }).Count -gt 0) {
        foreach ($m in $items) { if ($installed -contains $m.File.ToLower()) { $on[$m.Id] = $true } }
    } else {
        foreach ($m in $items) { if ($m.Category -ne "tools") { $on[$m.Id] = $true } }
    }
    while ($true) {
        Say ""
        Say "Choose mods. [x] means it will be installed. The Mod Manager (core) is always installed."
        $n = 0
        $last = ""
        foreach ($m in $items) {
            $n++
            if ($m.Category -ne $last) {
                $last = $m.Category
                $title = $CategoryTitles[$m.Category]
                if (-not $title) { $title = $m.Category }
                Say ""
                Say ("  " + $title) "Cyan"
            }
            $mark = " "
            if ($on.ContainsKey($m.Id)) { $mark = "x" }
            Say (("   {0,2}. [{1}] {2,-22} {3}" -f $n, $mark, (AsciiText $m.Name), (ShortText $m.Description)))
        }
        Say ""
        Say "Type numbers to switch mods on or off (for example: 3 7 12). a = all, n = none, q = quality of life only."
        $answer = Read-Host "Press Enter when you are done"
        if ([string]::IsNullOrWhiteSpace($answer)) { break }
        foreach ($tok in ($answer -split "[,; ]+")) {
            $t = $tok.Trim().ToLower()
            if (-not $t) { continue }
            if ($t -eq "a") { foreach ($m in $items) { $on[$m.Id] = $true }; continue }
            if ($t -eq "n") { $on.Clear(); continue }
            if ($t -eq "q") { $on.Clear(); foreach ($m in $items) { if ($m.Category -ne "tools") { $on[$m.Id] = $true } }; continue }
            $num = 0
            if ([int]::TryParse($t, [ref]$num) -and $num -ge 1 -and $num -le $items.Count) {
                $id = $items[$num - 1].Id
                if ($on.ContainsKey($id)) { $on.Remove($id) } else { $on[$id] = $true }
            } else { Warn ("Not a number from the list: " + $t) }
        }
    }
    return @($on.Keys)
}

function UpdateMods([string]$game, $bundled) {
    $source = $bundled
    if (-not $Offline) {
        try {
            Say "Looking for newer versions..."
            $remote = ReadManifest ([Text.Encoding]::UTF8.GetString((DownloadBytes $ManifestUrl)))
            # Take the newer of the online list and this download for each mod.
            $merged = New-Object System.Collections.Generic.List[object]
            foreach ($r in $remote) {
                $b = $bundled | Where-Object { $_.Id -eq $r.Id } | Select-Object -First 1
                if ($b -and ((ParseVersion $b.Version) -gt (ParseVersion $r.Version))) { $merged.Add($b) } else { $merged.Add($r) }
            }
            $source = $merged
        } catch {
            Warn ("Could not reach GitHub (" + $_.Exception.Message + "). Using the files in this download instead.")
        }
    }
    $installed = InstalledFiles $game
    $mine = @($source | Where-Object { $installed -contains $_.File.ToLower() })
    if ($mine.Count -eq 0) { Warn "No mods are installed yet. Pick 1, 2 or 3 first."; return }
    $dir = PluginDir $game
    $updated = 0
    # Core first: the mods need the matching core.
    foreach ($e in ($mine | Sort-Object { if ($_.Id -eq "core") { 0 } else { 1 } })) {
        $current = Join-Path $dir $e.File
        if ((Sha256Of $current) -eq $e.Sha256) { continue }
        PutMod $game $e
        Good ("Updated " + (AsciiText $e.Name) + " to " + $e.Version)
        $updated++
    }
    Say ""
    if ($updated -eq 0) { Say "Everything is up to date." "Green" } else { Say ($updated.ToString() + " mod(s) updated.") "Green" }
}

function UninstallAll([string]$game, $manifest) {
    $dir = PluginDir $game
    $files = @($manifest | ForEach-Object { $_.File.ToLower() })
    $removed = 0
    if (Test-Path -LiteralPath $dir) {
        foreach ($f in (Get-ChildItem -LiteralPath $dir -File)) {
            if ($files -contains $f.Name.ToLower() -or $f.Name.ToLower() -like "oth.*.dll.new") {
                Remove-Item -LiteralPath $f.FullName -Force
                $removed++
            }
        }
        $pending = Join-Path $dir ".pending"
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Recurse -Force }
        if (-not (Get-ChildItem -LiteralPath $dir -Force)) { Remove-Item -LiteralPath $dir -Force }
    }
    Good ("Removed " + $removed + " mod file(s).")

    $settings = $RemoveSettings
    if (-not $Action) { $settings = AskYesNo "Also delete the mods' settings files (BepInEx\config\oth.*.cfg)?" $false }
    if ($settings) {
        $cfg = Join-Path $game "BepInEx\config"
        if (Test-Path -LiteralPath $cfg) {
            foreach ($f in (Get-ChildItem -LiteralPath $cfg -File)) {
                if ($f.Name -like "oth.*.cfg" -or $f.Name -like "oth.core.enabled.txt*") { Remove-Item -LiteralPath $f.FullName -Force }
            }
        }
        Good "Removed the settings files. Your own folders (liveries, radio music, replays) were left alone."
    }

    $bep = $RemoveBepInEx
    if (-not $Action) {
        Say ""
        Say "BepInEx is the loader the mods run on. Removing it also removes every other BepInEx mod in this game."
        $bep = AskYesNo "Also remove BepInEx?" $false
    }
    if ($bep) {
        foreach ($name in @("BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version")) {
            $p = Join-Path $game $name
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
        }
        Good "Removed BepInEx."
    }
    Say ""
    Say "Uninstalled. The game itself was not touched." "Green"
}

function NextSteps([string]$game) {
    Say ""
    Say "Next steps" "Cyan"
    Say "  1. Start the game. The first start after installing takes a few minutes while BepInEx gets ready."
    Say "  2. In the game, press F7 (or Esc, then MODS) to open the mods page."
    Say "  3. If something is wrong, look at BepInEx\LogOutput.log in the game folder, and see the README."
}

function Menu {
    Say ""
    Say "What do you want to do?"
    Say "  1  Everything         all mods, including the solo and host tools"
    Say "  2  Quality of life    no cheats and no host tools"
    Say "  3  Choose mods        pick from a list"
    Say "  4  Update             bring the installed mods up to date"
    Say "  5  Uninstall          remove the mods"
    Say "  Q  Quit"
    $a = (Read-Host "Your choice").Trim().ToLower()
    switch ($a) {
        "1" { return "everything" }
        "2" { return "qol" }
        "3" { return "choose" }
        "4" { return "update" }
        "5" { return "uninstall" }
        "q" { return "quit" }
        default { return "" }
    }
}

# ---- main --------------------------------------------------------------------------------------------------------
try {
    Say ""
    Say "Over The Hill mods: installer" "Cyan"
    Say "Fan made, not affiliated with Funselektor."
    Say ""
    if ($PSVersionTable.PSVersion.Major -lt 5) { Fail "This needs Windows PowerShell 5 or newer (Windows 10 and 11 have it)." }

    $bundled = ReadBundledManifest
    $game = GetGameDir
    AssertGameClosed

    $act = $Action
    if (-not $act) {
        while ($true) {
            $act = Menu
            if ($act) { break }
            Warn "Please type 1, 2, 3, 4, 5 or Q."
        }
    }
    if ($act -eq "quit") { exit 0 }

    switch ($act) {
        "everything" {
            EnsureBepInEx $game
            InstallMods $game $bundled @($bundled | ForEach-Object { $_.Id })
            NextSteps $game
        }
        "qol" {
            EnsureBepInEx $game
            InstallMods $game $bundled @($bundled | Where-Object { $_.Category -ne "tools" } | ForEach-Object { $_.Id })
            NextSteps $game
        }
        "choose" {
            # (with -File a "-Mods a,b" arrives as one string)
            $ids = @($Mods | ForEach-Object { $_ -split "[,; ]+" } | Where-Object { $_ })
            if ($ids.Count -eq 0) {
                if ($Action) { Fail "-Action choose needs -Mods <ids>." }
                $ids = PickMods $bundled $game
            }
            $bad = @($ids | Where-Object { $id = $_.ToLower(); -not ($bundled | Where-Object { $_.Id -eq $id }) })
            if ($bad.Count -gt 0) { Fail ("Unknown mod id(s): " + ($bad -join ", ")) }
            EnsureBepInEx $game
            InstallMods $game $bundled @($ids | ForEach-Object { $_.ToLower() })
            NextSteps $game
        }
        "update" { UpdateMods $game $bundled }
        "uninstall" {
            if (-not $Action -and -not (AskYesNo "Remove the Over The Hill mods from this game?" $false)) { Say "Nothing was changed."; exit 0 }
            UninstallAll $game $bundled
        }
    }
    exit 0
} catch {
    Say ""
    Say ("Something went wrong: " + $_.Exception.Message) "Red"
    if ($_.Exception -is [UnauthorizedAccessException]) {
        Say "Windows would not let me write to the game folder. Right click Install.bat and choose Run as administrator, or move your Steam library to a folder you own."
    }
    Say "If this keeps happening, see the README (Something wrong?)."
    exit 1
}
