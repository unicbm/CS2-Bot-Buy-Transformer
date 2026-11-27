param([string]$Output = 'artifacts/CS2-Bot-Buy-Transformer-win-x64.zip', [switch]$Int8)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$build = Join-Path $root 'artifacts/plugin'
$target = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$prefix = 'addons/counterstrikesharp/plugins/BotBuy/'
$files = 'BotBuy.dll','BotBuy.deps.json','team-policy.json.gz','bot-native.json','buy-policy.json','pro-primary-policy.json','pro-transformer.json'
$docs = 'README.md','NOTICE.md','LICENSE','LICENSES/GPL-3.0.txt'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = $target + '.partial'
$stream = [IO.File]::Create($temp)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in $files + $docs) {
            $source = if ($name -in $files) { Join-Path $build $name } else { Join-Path $root $name }
            if ($Int8 -and $name -eq 'team-policy.json.gz') { $source = Join-Path $root 'models/team-policy-int8.json.gz' }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, $prefix + $name)
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Move-Item -LiteralPath $temp -Destination $target -Force
Write-Output $target
