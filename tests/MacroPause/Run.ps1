$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# Exercise the production loops without loading Dalamud or attaching to FFXIV.
$native = Get-Content (Join-Path $repo 'SomethingNeedDoing/NativeMacro/NativeMacroEngine.cs') -Raw
$start = $native.IndexOf('    private async Task ExecuteMacro(')
$end = $native.IndexOf('    protected virtual void OnMacroError', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Cannot locate native execution loop.' }
$nativeLoop = $native.Substring($start, $end - $start).Replace('private async Task ExecuteMacro', 'public async Task ExecuteMacro')

$lua = Get-Content (Join-Path $repo 'SomethingNeedDoing/LuaMacro/NLuaMacroEngine.cs') -Raw
$luaMatch = [regex]::Match($lua, '(?ms)^                    while \(!token.IsCancellationRequested\).*?(?=^                }\r?\n                catch \(OperationCanceledException\))')
if (-not $luaMatch.Success) { throw 'Cannot locate Lua execution loop.' }
$luaLoop = $luaMatch.Value

$pause = 'await MacroPause.WaitAsync(instance.PauseEvent, token);'
if (-not $nativeLoop.Contains($pause) -or -not $luaLoop.Contains($pause)) { throw 'Both engines must use the shared asynchronous pause wait.' }
$blocking = 'instance.PauseEvent.Wait(token);'
$support = Get-Content (Join-Path $PSScriptRoot 'Harness.cs') -Raw
$compiled = $support.Replace('/* NATIVE_LOOP */', $nativeLoop).
    Replace('/* LUA_LOOP */', $luaLoop).
    Replace('/* BASELINE_NATIVE_LOOP */', $nativeLoop.Replace($pause, $blocking)).
    Replace('/* BASELINE_LUA_LOOP */', $luaLoop.Replace($pause, $blocking))

# Keep generated compiler input under the repository's ignored bin directory.
$generatedDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
$generated = Join-Path $generatedDir 'GeneratedHarness.cs'
Set-Content -LiteralPath $generated -Value $compiled -Encoding utf8
Add-Type -Path @($generated, (Join-Path $repo 'SomethingNeedDoing/Utils/MacroPause.cs'))
[SndPauseTests.Harness]::RunAll()
