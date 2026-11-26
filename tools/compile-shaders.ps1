<#
.SYNOPSIS
Compiles the mount's shaders with s&box's command line shader compiler, without the editor.

.DESCRIPTION
s&box only renders with compiled shaders (<name>.shader_c next to <name>.shader), and only the editor
or bin/managed/ShaderCompiler.exe can compile them: the game can't. So the mount ships them compiled.

ShaderCompiler.exe only compiles .shader files under the s&box folder it runs from, which it finds
from its own path. To compile without writing to the s&box install, this runs it from a stand-in
folder in %TEMP%: bin and core are junctions to the real install, and mounts/blam/Assets/shaders holds
a copy of the shaders. The compiler writes its caches and logs into that folder, and nothing into
bin or core. The junctions only exist while the compiler runs. A killed run can leave them behind
(the next run removes them): remove them with rmdir, never Windows PowerShell's Remove-Item -Recurse,
which follows junctions into the install.

Compiled shaders are built against the shader includes of the s&box that compiled them, so they need
compiling again after s&box updates. A change to s&box's .version recompiles them all; otherwise
the compiler skips shaders that are up to date.

Writes <name>.shader and <name>.shader_c for every shader to -OutDir.

Some shaders are compiled a second time as a variant with a define of their own (see $variants),
for variants that can't be a static combo: halo1_model_camo.shader is halo1_model.shader with
HALO1_MODEL_CAMOUFLAGED, which makes it translucent, and s&box decides that per shader.
halo1_model_skin.shader (HALO1_MODEL_SKIN) draws MCC's weapon skins: a variant rather than a
combo, so the skins' extra textures and shading don't cost the other models anything.
halo2_opaque_camo.shader (HALO2_OPAQUE_CAMO) is Halo 2's camouflaged model variant, translucent like
halo1_model_camo.shader. halo3_shader_transparent.shader (HALO3_TRANSPARENT) is Halo 3's render method shader
for its blended modes (translucent, blend mode as a combo), halo3_terrain.shader (HALO3_TERRAIN) its terrain
(four materials by a blend map), halo3_water.shader (HALO3_WATER) its water (transparent, reading a copy
of the frame for its refraction, which only the water should pay for).

.EXAMPLE
tools\compile-shaders.ps1 -OutDir out\shaders
#>
param(
	# Where to write the shaders and their compiled versions.
	[Parameter( Mandatory )] [string] $OutDir,

	# The shaders to compile. Defaults to the mount's.
	[string] $Source,

	# The s&box install whose compiler and shader includes to use.
	[string] $Engine = $(if ( $env:FACEPUNCH_ENGINE ) { $env:FACEPUNCH_ENGINE } else { "C:\Program Files (x86)\Steam\steamapps\common\sbox" }),

	# Recompile every shader, even ones that are up to date.
	[switch] $Force
)

$ErrorActionPreference = "Stop"
if ( -not $Source ) { $Source = Join-Path (Split-Path $PSScriptRoot -Parent) "src\Sandbox.Mounting.Blam\Assets\shaders" }
$Source = (Resolve-Path $Source).Path
$Engine = (Resolve-Path $Engine).Path.TrimEnd( "\" )

if ( -not (Test-Path (Join-Path $Engine "bin\managed\ShaderCompiler.exe")) ) {
	Write-Host "s&box's shader compiler isn't at $Engine\bin\managed\ShaderCompiler.exe." -ForegroundColor Red
	exit 1
}

# One stand-in folder per checkout, kept between runs so up to date shaders are skipped.
$sha = [Security.Cryptography.SHA1]::Create()
$id = [BitConverter]::ToString( $sha.ComputeHash( [Text.Encoding]::UTF8.GetBytes( $Source.ToLowerInvariant() ) ) ).Replace( "-", "" ).Substring( 0, 8 )
$root = Join-Path ([IO.Path]::GetTempPath()) "sbox-blam-shaders-$id"
$work = Join-Path $root "mounts\blam\Assets\shaders"
New-Item -ItemType Directory -Force $work | Out-Null

# The compiler's file system mounts every addon's Assets folder the install has (citizen, content, ...) and fails on a missing one, so give each an empty stand-in.
foreach ( $addon in Get-ChildItem (Join-Path $Engine "addons") -Directory ) {
	if ( Get-ChildItem $addon.FullName -Directory | Where-Object Name -eq "Assets" ) { New-Item -ItemType Directory -Force (Join-Path $root "addons\$($addon.Name)\Assets") | Out-Null }
}

function Get-Hash( $path ) { if ( Test-Path $path ) { (Get-FileHash $path).Hash } else { $null } }

# Removes a junction without touching what it points to. Never Remove-Item -Recurse a junction.
function Remove-Junction( $path ) {
	$item = Get-Item $path -Force -ErrorAction SilentlyContinue
	if ( $null -eq $item ) { return }
	if ( -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) ) { throw "$path should be a junction but isn't." }
	cmd /c rmdir "$path"
	if ( Test-Path $path ) { throw "Couldn't remove the junction $path." }
}

# Shader variants: a copy of a shader with a define added at the top of its COMMON section.
$variants = @(
	@{ Name = "halo1_model_camo.shader"; From = "halo1_model.shader"; Define = "HALO1_MODEL_CAMOUFLAGED" },
	@{ Name = "halo1_model_skin.shader"; From = "halo1_model.shader"; Define = "HALO1_MODEL_SKIN" },
	@{ Name = "halo2_opaque_camo.shader"; From = "halo2_opaque.shader"; Define = "HALO2_OPAQUE_CAMO" },
	@{ Name = "halo3_shader_transparent.shader"; From = "halo3_shader.shader"; Define = "HALO3_TRANSPARENT" },
	@{ Name = "halo3_terrain.shader"; From = "halo3_shader.shader"; Define = "HALO3_TERRAIN" },
	@{ Name = "halo3_water.shader"; From = "halo3_shader.shader"; Define = "HALO3_WATER" }
)

# Mirror the shaders (and write their variants) into the stand-in folder.
$shaders = @(Get-ChildItem $Source -Filter *.shader | ForEach-Object { $_.Name })
foreach ( $variant in $variants ) {
	$text = [IO.File]::ReadAllText( (Join-Path $Source $variant.From) )
	$text = [regex]::Replace( $text, "(?m)^COMMON\s*\r?\n\{\r?\n", { param( $m ) "$($m.Value)`t#define $($variant.Define) 1`r`n" } )
	if ( $text -notmatch [regex]::Escape( "#define $($variant.Define) 1" ) ) { throw "Couldn't add $($variant.Define) to $($variant.From)." }
	$target = Join-Path $work $variant.Name
	if ( -not (Test-Path $target) -or [IO.File]::ReadAllText( $target ) -ne $text ) { [IO.File]::WriteAllText( $target, $text ) }
	$shaders += $variant.Name
}
Get-ChildItem $work -File | Where-Object { $shaders -notcontains ($_.Name -replace "_c$", "") } | Remove-Item
foreach ( $shader in Get-ChildItem $Source -Filter *.shader ) {
	$target = Join-Path $work $shader.Name
	if ( (Get-Hash $target) -ne (Get-Hash $shader.FullName) ) { Copy-Item $shader.FullName $target -Force }
}

# A different s&box build may have different shader includes: compile everything again.
$version = Get-Content (Join-Path $Engine ".version") -Raw -ErrorAction SilentlyContinue
$stamp = Join-Path $root "sbox.version"
if ( $version -ne (Get-Content $stamp -Raw -ErrorAction SilentlyContinue) ) { $Force = $true }

$arguments = @($shaders | ForEach-Object { Join-Path $work $_ })
if ( $Force ) { $arguments += "-f" }

Write-Host "Compiling shaders with $Engine\bin\managed\ShaderCompiler.exe..."
# s&box writes its logs to $env:FACEPUNCH_ENGINE\logs (the launcher sets it for the user), so point
# that at the stand-in folder too.
$facepunchEngine = $env:FACEPUNCH_ENGINE
$env:FACEPUNCH_ENGINE = $root
try {
	foreach ( $folder in "bin", "core" ) {
		Remove-Junction (Join-Path $root $folder)
		New-Item -ItemType Junction -Path (Join-Path $root $folder) -Target (Join-Path $Engine $folder) | Out-Null
	}

	# The compiler logs to stderr too, which Windows PowerShell turns into errors.
	$ErrorActionPreference = "Continue"
	$output = & (Join-Path $root "bin\managed\ShaderCompiler.exe") $arguments 2>&1 | ForEach-Object { "$_" }
	$exitCode = $LASTEXITCODE
	$ErrorActionPreference = "Stop"
} finally {
	$env:FACEPUNCH_ENGINE = $facepunchEngine
	foreach ( $folder in "bin", "core" ) { Remove-Junction (Join-Path $root $folder) }
}

# On failure, show what the shaders that failed printed (or everything, if the compiler itself
# failed), each line starting with "ShaderCompiler: " so the build reports them as errors.
if ( $exitCode -ne 0 ) {
	$lines = @($output | Where-Object { $_.Trim() -and $_ -notmatch " engine/V " })
	$failed = @()
	$section = @()
	foreach ( $line in $lines ) {
		if ( $line -match "^\(\d+/\d+\) " ) { $section = @() }
		$section += $line
		if ( $line -match "^\s*Compile failed\." ) { $failed += $section }
	}
	if ( -not $failed ) { $failed = $lines }
	$failed | ForEach-Object { Write-Host "ShaderCompiler: $_" }
	Write-Host "ShaderCompiler: shader compile failed (exit code $exitCode)." -ForegroundColor Red
	exit 1
}

# The compiler's own progress lines; the engine's timestamped log lines are noise here.
$output | Where-Object { $_ -notmatch "\d\d:\d\d:\d\d " -and $_ -notmatch "^\s+VFX_PROGRAM_" } | Write-Host
[IO.File]::WriteAllText( $stamp, "$version" )

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ( $shader in $shaders ) {
	foreach ( $name in $shader, "$($shader)_c" ) {
		$from = Join-Path $work $name
		$to = Join-Path $OutDir $name
		if ( -not (Test-Path $from) ) { Write-Host "$name wasn't compiled." -ForegroundColor Red; exit 1 }
		if ( (Get-Hash $from) -ne (Get-Hash $to) ) { Copy-Item $from $to -Force }
	}
}
