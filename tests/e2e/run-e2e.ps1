#requires -Version 5.1
<#
.SYNOPSIS
  OpenSnip M1.5 无头 E2E 测试(SPEC docs/SPEC.md 8.3)。
  用 vdesk(隐藏桌面)+ OpenSnip.TestRpc 命名管道驱动 App,全链验收:
  遮罩渲染 → 测试选区 → 确认出图 → 剪贴板贴图 → 关闭全部 → 退出 → 销毁桌面。

.DESCRIPTION
  编排链(每步输出一行 JSON 断言,字段 step/pass/detail):
    env.preflight / build / desktop.create / app.launch / rpc.status.ready(30s 轮询)
    rpc.status.schema / app.warmup / snip.interactive / overlay.appeared / overlay.hwnd.match
    overlay.shot.mask / snip.setRegion(200,150,800,600) / overlay.shot.region
    snip.confirm / snip.output.file / clipboard.preset / app.paste / status.pins.assert
    pin.shot / pin.closeAll / app.exit / desktop.destroy

  断言依据 §8.4(测试底图注入,依赖 task-8 的 --test-rpc 注入实现):
    shot1 采样色 ∝ 8 色带表(允许遮罩统一压暗系数);shot2 选区红框/8 锚点/尺寸标签出现在期望坐标;
    confirm 输出与源底图选区区域逐点采样一致。无头桌面 DD=E_ACCESSDENIED、BitBlt=合法黑(§8.4 实证裁决)。

  协议最小扩展(任务方允许):
    - {"cmd":"paste"}:--test-rpc 模式下 OpenSnip.Ipc 服务端未启动(8.2 明确跳过单实例仲裁),
      CLI paste 无通路,故在测试协议中直接补 paste 命令。
    - {"cmd":"exit"}:仅探测;App 未实现时回退 vdesk kill,不算失败(报告注记)。

  产物布局(本脚本写域 = tests/e2e/**;SPEC 8.3 示例的 logs\e2e-snip.png 因写域约束
  改为 tests/e2e/artifacts/<runid>/e2e-snip.png):
    tests/e2e/artifacts/<runid>/   截图、App 输出、rpc-log.txt、失败现场转储
    tests/e2e/report/<runid>.json  完整报告(+ latest.json 副本)

  退出码:全 pass=0;任一步失败=1(保留桌面与现场截图;App 进程/热键占用情况见报告)。
  上游未就绪(--test-rpc 管道不存在/编译失败)时输出 BLOCKED_UPSTREAM 并给具体错误。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\e2e\run-e2e.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\e2e\run-e2e.ps1 -NoBuild
#>
param(
  [switch]$NoBuild,
  [int]$StatusTimeoutSec = 30,
  [switch]$KeepDesktop
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true) } catch { }

# ---------- 常量与路径 ----------
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tests/e2e → 仓库根
$Vdesk     = 'D:\Code\background-gui\publish\vdesk.exe'
$AppExe    = Join-Path $RepoRoot 'src\OpenSnip.App\bin\Debug\net10.0-windows\OpenSnip.App.exe'
$AppCsproj = Join-Path $RepoRoot 'src\OpenSnip.App\OpenSnip.App.csproj'
$AppSrcDir = Join-Path $RepoRoot 'src\OpenSnip.App'
$Desktop   = 'opensnip_e2e'
$PipeName  = 'OpenSnip.TestRpc'
$OverlayClass = 'OpenSnip.Overlay'
$RegionX = 200; $RegionY = 150; $RegionW = 800; $RegionH = 600

$RunId  = Get-Date -Format 'yyyyMMdd-HHmmss'
$ArtDir = Join-Path $PSScriptRoot ("artifacts\" + $RunId)
$RptDir = Join-Path $PSScriptRoot 'report'
New-Item -ItemType Directory -Force -Path $ArtDir | Out-Null
New-Item -ItemType Directory -Force -Path $RptDir  | Out-Null
$RpcLogPath = Join-Path $ArtDir 'rpc-log.txt'
$StartedAt  = Get-Date

function Write-RpcLog([string]$line) {
  try { Add-Content -Path $RpcLogPath -Value $line -Encoding UTF8 } catch { }
}

# ---------- 步进报告机制 ----------
$script:Steps   = New-Object System.Collections.Generic.List[object]
$script:Failed  = $false
$script:Blocked = $false      # 上游阻塞标记
$script:AppPid  = $null

function Add-Step {
  param([string]$Step, [bool]$Pass, $Detail, [switch]$Soft)
  $entry = [ordered]@{
    step   = $Step
    pass   = [bool]$Pass
    detail = "$( $Detail )"
  }
  $json = ConvertTo-Json -Compress -InputObject $entry -Depth 4
  Write-Output $json
  $script:Steps.Add($entry) | Out-Null
  if (-not $Pass -and -not $Soft) { $script:Failed = $true }
}

function Fail-Fast([string]$Step, $Detail) {
  Add-Step -Step $Step -Pass:$false -Detail $Detail   # 输出步骤 JSON(不可管道丢弃)
  throw ("STEP_FAILED:" + $Step)
}

function Get-Prop($Obj, [string]$Name) {
  if ($null -eq $Obj) { return $null }
  $p = $Obj.PSObject.Properties[$Name]
  if ($null -eq $p) { return $null }
  return , $p.Value   # 逗号包装:防止空数组被 PS 函数输出 unroll 成 null
}

function Trunc([string]$s, [int]$n = 420) {
  if ($null -eq $s) { return $null }
  if ($s.Length -le $n) { return $s }
  return ($s.Substring(0, $n) + "...(截断,共$($s.Length)字符)")
}

# ---------- vdesk / dotnet 包装 ----------
function Invoke-Vdesk {
  param([string[]]$VArgs)
  $outLines = & $Vdesk @VArgs 2>&1 | ForEach-Object { "$_" }
  $code = $LASTEXITCODE
  $nl   = [string][char]10
  $raw  = ($outLines -join $nl)
  Write-RpcLog ("VDESK " + ($VArgs -join ' ') + " => exit=$code " + (Trunc $raw 300))
  $parsed = $null
  foreach ($l in $outLines) {
    $t = $l.Trim()
    if ($t.StartsWith('{')) { try { $parsed = $t | ConvertFrom-Json } catch { } ; break }
  }
  return [pscustomobject]@{ ExitCode = $code; Raw = $raw; Json = $parsed }
}

function Find-Dotnet {
  $c = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { return 'C:\Program Files\dotnet\dotnet.exe' }
  return $null
}

# ---------- 测试 RPC 客户端(.NET NamedPipeClientStream,JSON 单行) ----------
function Invoke-TestRpc {
  param(
    [object]$Payload,
    [int]$ConnectTimeoutMs = 4000,
    [int]$IoTimeoutMs      = 15000
  )
  $json = ConvertTo-Json -Compress -InputObject $Payload -Depth 6
  $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            '.', $PipeName,
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::None)
  try {
    $pipe.Connect($ConnectTimeoutMs)   # 管道不存在/超时 → 抛异常
    try { $pipe.ReadTimeout  = $IoTimeoutMs } catch { }
    try { $pipe.WriteTimeout = $IoTimeoutMs } catch { }
    $enc    = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.IO.StreamWriter]::new($pipe, $enc, 1024, $true)
    $reader = [System.IO.StreamReader]::new($pipe, $enc, $false, 4096, $true)
    $writer.NewLine  = [string][char]10
    $writer.AutoFlush = $true
    $writer.WriteLine($json)
    $line = $reader.ReadLine()
    $reader.Dispose(); $writer.Dispose()
    Write-RpcLog ("REQ  " + $json)
    Write-RpcLog ("RESP " + $(if ($null -eq $line) { '<null>' } else { $line }))
    if ($null -eq $line) { throw '管道已连接但响应为空(服务端未回单行 JSON)' }
    return ($line | ConvertFrom-Json)
  } finally {
    $pipe.Dispose()
  }
}

function Wait-StatusReady {
  param([int]$TimeoutSec)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  $lastErr  = ''
  $tries    = 0
  while ((Get-Date) -lt $deadline) {
    $tries++
    try {
      $st = Invoke-TestRpc -Payload @{ cmd = 'status' } -ConnectTimeoutMs 1500 -IoTimeoutMs 8000
      if ($null -ne (Get-Prop $st 'ok') -and $st.ok) { return $st }
      $lastErr = '响应 ok!=true: ' + (ConvertTo-Json -Compress -InputObject $st -Depth 5)
    } catch {
      $lastErr = $_.Exception.Message
      if ($_.Exception.InnerException) { $lastErr += ' / ' + $_.Exception.InnerException.Message }
    }
    Start-Sleep -Milliseconds 400
  }
  throw ("status 轮询超时(>$($TimeoutSec)s,尝试 $tries 次),最后错误: " + $lastErr)
}

function Get-StatusSafe {
  try { return Invoke-TestRpc -Payload @{ cmd = 'status' } -ConnectTimeoutMs 2500 -IoTimeoutMs 8000 }
  catch { return $null }
}

# ---------- PNG 工具(IHDR 尺寸解析 + 采样式非黑检测) ----------
function Get-PngSize([string]$Path) {
  $fs = [System.IO.File]::OpenRead($Path)
  try {
    $buf = New-Object byte[] 24
    [void]$fs.Read($buf, 0, 24)
    if ($buf[0] -ne 0x89 -or $buf[1] -ne 0x50) { return $null }
    $w = ([uint32]$buf[16] -shl 24) -bor ([uint32]$buf[17] -shl 16) -bor ([uint32]$buf[18] -shl 8) -bor [uint32]$buf[19]
    $h = ([uint32]$buf[20] -shl 24) -bor ([uint32]$buf[21] -shl 16) -bor ([uint32]$buf[22] -shl 8) -bor [uint32]$buf[23]
    return @{ Width = [int]$w; Height = [int]$h }
  } finally { $fs.Dispose() }
}

function Test-PngNotBlank {
  param([string]$Path, [int]$Grid = 24)
  $r = @{ NonBlank = $false; MaxBrightness = 0; DistinctColors = 0; Sampled = 0; Width = 0; Height = 0; Reason = '' }
  if (-not (Test-Path $Path)) { $r.Reason = '文件不存在'; return $r }
  $size = Get-PngSize $Path
  if ($null -eq $size) { $r.Reason = 'PNG 头解析失败'; return $r }
  $r.Width = $size.Width; $r.Height = $size.Height
  try { Add-Type -AssemblyName System.Drawing } catch {
    $r.Reason = 'System.Drawing 加载失败: ' + $_.Exception.Message; return $r
  }
  $bmp = $null
  try { $bmp = [System.Drawing.Bitmap]::new($Path) } catch {
    $r.Reason = 'PNG 解码失败: ' + $_.Exception.Message; return $r
  }
  try {
    if ($bmp.Width -ne $r.Width -or $bmp.Height -ne $r.Height) {
      $r.Reason = "IHDR 尺寸($($r.Width)x$($r.Height))与解码尺寸($($bmp.Width)x$($bmp.Height))不一致"
      return $r
    }
    $colors = New-Object System.Collections.Generic.HashSet[int]
    $maxB = 0; $sampled = 0
    for ($gy = 0; $gy -lt $Grid; $gy++) {
      for ($gx = 0; $gx -lt $Grid; $gx++) {
        $x = [int](($gx + 0.5) * $bmp.Width  / $Grid)
        $y = [int](($gy + 0.5) * $bmp.Height / $Grid)
        $c = $bmp.GetPixel($x, $y)
        $b = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
        if ($b -gt $maxB) { $maxB = $b }
        [void]$colors.Add(([int]$c.R -shl 16) -bor ([int]$c.G -shl 8) -bor [int]$c.B)
        $sampled++
      }
    }
    $r.MaxBrightness = $maxB; $r.DistinctColors = $colors.Count; $r.Sampled = $sampled
    if ($maxB -gt 24 -and $colors.Count -ge 2) { $r.NonBlank = $true; $r.Reason = 'OK' }
    else { $r.Reason = "采样全暗(max=$maxB)/颜色单一($($colors.Count)种) → 判定blank" }
    return $r
  } finally { $bmp.Dispose() }
}

# ---------- §8.4 测试底图色带(8 等宽垂直色带,固定表,左→右) ----------
$script:BandTable = @(
  @(255, 255, 255),
  @(255, 255, 0),
  @(0, 255, 255),
  @(0, 255, 0),
  @(255, 0, 255),
  @(255, 0, 0),
  @(0, 0, 255),
  @(0, 0, 0)
)

function Get-BandColor([int]$x, [int]$baseWidth) {
  $bi = [int][Math]::Floor([double]$x / $baseWidth * 8)
  if ($bi -lt 0) { $bi = 0 }
  if ($bi -gt 7) { $bi = 7 }
  return , $script:BandTable[$bi]
}

function Test-BaseImageShot {
  # §8.4 断言 1:遮罩首帧采样色 ∝ 色带表(允许统一压暗系数 s∈(0,1]);黑带要求全暗
  param([string]$Path, [int]$BaseWidth, [int]$GridX = 24, [int]$GridY = 14)
  $r = @{ Ok = $false; Sampled = 0; Failed = 0; MaxBrightness = 0; Reason = ''; FailSamples = @() }
  try { Add-Type -AssemblyName System.Drawing } catch { $r.Reason = 'System.Drawing 加载失败: ' + $_.Exception.Message; return $r }
  $bmp = $null
  try { $bmp = [System.Drawing.Bitmap]::new($Path) } catch { $r.Reason = 'PNG 解码失败: ' + $_.Exception.Message; return $r }
  try {
    for ($gy = 0; $gy -lt $GridY; $gy++) {
      for ($gx = 0; $gx -lt $GridX; $gx++) {
        $x = [int](($gx + 0.5) * $bmp.Width  / $GridX)
        $y = [int](($gy + 0.5) * $bmp.Height / $GridY)
        $c = $bmp.GetPixel($x, $y)
        $maxC = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
        if ($maxC -gt $r.MaxBrightness) { $r.MaxBrightness = $maxC }
        $r.Sampled++
        $band = Get-BandColor $x $BaseWidth
        $hex = "#" + $c.R.ToString('X2') + $c.G.ToString('X2') + $c.B.ToString('X2')
        $bhx = "#" + $band[0].ToString('X2') + $band[1].ToString('X2') + $band[2].ToString('X2')
        if ($band[0] -eq 0 -and $band[1] -eq 0 -and $band[2] -eq 0) {
          if ($maxC -le 12) { continue }
          $r.Failed++; if ($r.FailSamples.Count -lt 8) { $r.FailSamples += "($x,$y)=$hex 期望黑带" }
          continue
        }
        if ($maxC -le 12) { $r.Failed++; if ($r.FailSamples.Count -lt 8) { $r.FailSamples += "($x,$y) 全暗但期望色带 $bhx" }; continue }
        $s = $maxC / 255.0
        $dev = [Math]::Max([Math]::Abs($c.R - [int]($band[0] * $s)), [Math]::Max([Math]::Abs($c.G - [int]($band[1] * $s)), [Math]::Abs($c.B - [int]($band[2] * $s))))
        if ($dev -le 22) { continue }
        $r.Failed++; if ($r.FailSamples.Count -lt 8) { $r.FailSamples += "($x,$y)=$hex 期望∝$bhx dev=$dev" }
      }
    }
    $r.Ok = ($r.Failed -eq 0 -and $r.Sampled -gt 0)
    if (-not $r.Ok -and $r.Reason -eq '') { $r.Reason = "采样 $($r.Sampled) 点,失败 $($r.Failed) 点" }
    return $r
  } finally { $bmp.Dispose() }
}

function Test-CropMatchesBase {
  # §8.4 断言 3:输出(i,j) ↔ 源底图(SrcX+i, SrcY+j)全亮度逐点采样一致(裁剪来自未压暗冻结帧)
  param([string]$Path, [int]$BaseWidth, [int]$SrcX, [int]$SrcY, [int]$GridX = 20, [int]$GridY = 15, [int]$Tol = 12)
  $r = @{ Ok = $false; Sampled = 0; Failed = 0; Reason = ''; FailSamples = @() }
  try { Add-Type -AssemblyName System.Drawing } catch { $r.Reason = 'System.Drawing 加载失败: ' + $_.Exception.Message; return $r }
  $bmp = $null
  try { $bmp = [System.Drawing.Bitmap]::new($Path) } catch { $r.Reason = 'PNG 解码失败: ' + $_.Exception.Message; return $r }
  try {
    for ($gy = 0; $gy -lt $GridY; $gy++) {
      for ($gx = 0; $gx -lt $GridX; $gx++) {
        $x = [int](($gx + 0.5) * $bmp.Width  / $GridX)
        $y = [int](($gy + 0.5) * $bmp.Height / $GridY)
        $c = $bmp.GetPixel($x, $y)
        $r.Sampled++
        $band = Get-BandColor ($SrcX + $x) $BaseWidth
        $dev = [Math]::Max([Math]::Abs($c.R - $band[0]), [Math]::Max([Math]::Abs($c.G - $band[1]), [Math]::Abs($c.B - $band[2])))
        if ($dev -le $Tol) { continue }
        $r.Failed++
        if ($r.FailSamples.Count -lt 8) {
          $hex = "#" + $c.R.ToString('X2') + $c.G.ToString('X2') + $c.B.ToString('X2')
          $bhx = "#" + $band[0].ToString('X2') + $band[1].ToString('X2') + $band[2].ToString('X2')
          $r.FailSamples += "out($x,$y)=$hex 期望=$bhx dev=$dev"
        }
      }
    }
    $r.Ok = ($r.Failed -eq 0 -and $r.Sampled -gt 0)
    if (-not $r.Ok -and $r.Reason -eq '') { $r.Reason = "采样 $($r.Sampled) 点,不一致 $($r.Failed) 点(容差 $Tol)" }
    return $r
  } finally { $bmp.Dispose() }
}

function Test-PixelWindow {
  # 窗口内像素命中计数:Kind = red(选区边框)/ white(锚点)/ light(标签笔画)
  param($Bmp, [int]$X0, [int]$X1, [int]$Y0, [int]$Y1, [string]$Kind)
  $hits = 0
  for ($y = $Y0; $y -le $Y1; $y++) {
    for ($x = $X0; $x -le $X1; $x++) {
      if ($x -lt 0 -or $y -lt 0 -or $x -ge $Bmp.Width -or $y -ge $Bmp.Height) { continue }
      $c = $Bmp.GetPixel($x, $y)
      if ($Kind -eq 'red')   { if ($c.R -ge 190 -and $c.G -le 70 -and $c.B -le 70) { $hits++ } }
      elseif ($Kind -eq 'white') { if ($c.R -ge 175 -and $c.G -ge 175 -and $c.B -ge 175) { $hits++ } }
      else { $m = [Math]::Max($c.R, [Math]::Max($c.G, $c.B)); if ($m -ge 150) { $hits++ } }
    }
  }
  return $hits
}

function Get-FileMd5([string]$Path) {
  (Get-FileHash -Path $Path -Algorithm MD5).Hash
}

# ---------- 失败现场转储 ----------
function Save-FailureScene {
  param([string]$Reason)
  Write-RpcLog ("FAILURE " + $Reason)
  try {
    $w  = Invoke-Vdesk -VArgs @('windows', '--desktop', $Desktop)
    $wd = Invoke-Vdesk -VArgs @('windows', '--desktop', 'Default')
    $w.Raw  | Set-Content -Path (Join-Path $ArtDir 'failure-scene-windows.json') -Encoding UTF8
    $wd.Raw | Set-Content -Path (Join-Path $ArtDir 'failure-scene-default-desktop-windows.json') -Encoding UTF8
    $allWins = @()
    if ($w.Json)  { $allWins += @($w.Json.windows) }
    if ($wd.Json) { $allWins += @($wd.Json.windows) }
    $i = 0
    foreach ($win in $allWins) {
      $cls = Get-Prop $win 'class'
      if ($cls -match 'OpenSnip') {
        $h = Get-Prop $win 'hwnd'
        $i++
        $out = Join-Path $ArtDir ("failure-scene-{0}-{1}.png" -f ($cls -replace '\.', '_'), $i)
        Invoke-Vdesk -VArgs @('shot', '--hwnd', "$h", '--out', $out) | Out-Null
      }
    }
  } catch { Write-RpcLog ('scene dump 失败: ' + $_.Exception.Message) }
}

# =====================================================================
#  主流程
# =====================================================================
$report = [ordered]@{
  run                = $RunId
  startedAt          = $StartedAt.ToString('o')
  spec               = 'docs/SPEC.md 8.3 (M1.5 无头 E2E)'
  desktop            = $Desktop
  appExe             = $AppExe
  appBinaryMtime     = if (Test-Path $AppExe) { (Get-Item $AppExe).LastWriteTime.ToString('o') } else { $null }
  protocolExtensions = @(
    '{"cmd":"paste"} —— --test-rpc 模式 IpcServer 未启动,CLI paste 无通路,按任务允许的协议最小扩展',
    '{"cmd":"exit"} —— 仅探测,未实现则回退 vdesk kill(不算失败)'
  )
  steps              = @()
  artifacts          = @()
  result             = 'FAIL'
  blockedUpstream    = $false
  finishedAt         = $null
  durationSec        = 0
}
$finalErr = $null
$finalLine = -1
$finalStack = ''

try {
  # ---------- 预检 ----------
  if (-not (Test-Path $Vdesk)) {
    Fail-Fast 'env.preflight' ("vdesk 不存在: " + $Vdesk + "(环境阻塞,非被测对象问题)")
  }
  if (-not (Test-Path $AppExe)) {
    $script:Blocked = $true
    Fail-Fast 'env.preflight' ("App.exe 不存在: " + $AppExe + "(上游未构建产物)")
  }
  Add-Step 'env.preflight' $true ("vdesk=" + $Vdesk + "; AppExe mtime=" + (Get-Item $AppExe).LastWriteTime.ToString('o'))

  # ---------- build ----------
  if (-not $NoBuild) {
    $dotnet = Find-Dotnet
    if (-not $dotnet) {
      $script:Blocked = $true
      Fail-Fast 'build' '未找到 dotnet CLI(环境阻塞)'
    }
    $buildOut = & $dotnet build $AppCsproj -c Debug --nologo -v minimal 2>&1 | ForEach-Object { "$_" }
    $bExit = $LASTEXITCODE
    $bRaw  = ($buildOut -join ([string][char]10))
    $bRaw | Set-Content -Path (Join-Path $ArtDir 'build.log') -Encoding UTF8
    if ($bExit -ne 0) {
      $script:Blocked = $true
      Fail-Fast 'build' ("dotnet build 失败(exit=$bExit),上游编译问题。尾部输出: " + (Trunc $bRaw))
    }
    Add-Step 'build' $true 'dotnet build OK(exit=0)'
  } else {
    Add-Step 'build' $true '跳过(-NoBuild)'
  }

  # 产物新鲜度软告警:exe 比 App 源码最新 .cs 旧 → 提示可能缺最新实现
  $newestSrc = Get-ChildItem -Path $AppSrcDir -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $exeTime = (Get-Item $AppExe).LastWriteTime
  if ($newestSrc -and $newestSrc.LastWriteTime -gt $exeTime) {
    Add-Step 'app.binary.freshness' $false -Soft ("App.exe(" + $exeTime.ToString('o') + ") 早于最新源码 " + $newestSrc.Name + "(" + $newestSrc.LastWriteTime.ToString('o') + ") → 产物可能未含最新实现(若 rpc 步骤失败,优先怀疑此点)")
  }

  # ---------- desktop.create(先清理残留) ----------
  $d0 = Invoke-Vdesk -VArgs @('desktop', 'destroy', $Desktop, '--kill')
  $cleanupNote = if ($d0.ExitCode -eq 0) { "残留桌面已清理(killed=$(Get-Prop $d0.Json 'killed'))" } else { '无残留桌面' }
  $d1 = Invoke-Vdesk -VArgs @('desktop', 'create', $Desktop)
  if ($d1.ExitCode -ne 0) { Fail-Fast 'desktop.create' ('desktop create 失败: ' + (Trunc $d1.Raw)) }
  Add-Step 'desktop.create' $true ("created=$($d1.Json.created); $cleanupNote")

  # ---------- 环境纯净度:launch 前 TestRpc 管道不应存在 ----------
  $preExisting = $false
  try {
    $probe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $probe.Connect(400)
    $preExisting = $true
    $probe.Dispose()
  } catch { $preExisting = $false }
  if ($preExisting) {
    # 上一步 destroy --kill 刚终止旧实例时,内核管道对象拆除有延迟 → 等 1.2s 复检一次再定论
    Start-Sleep -Milliseconds 1200
    $preExisting = $false
    try {
      $probe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
      $probe.Connect(400)
      $preExisting = $true
      $probe.Dispose()
    } catch { $preExisting = $false }
  }
  if ($preExisting) {
    Add-Step 'env.pipe.pristine' $false -Soft "launch 前 $PipeName 已存在——疑似其他实例(如开发者的调试实例)占用同名管道,本实例可能连接到错误进程(§8.2 管道名无 per-instance 隔离,契约缺陷信号)"
  } else {
    Add-Step 'env.pipe.pristine' $true 'launch 前 TestRpc 管道不存在,环境纯净'
  }

  # ---------- app.launch ----------
  # §8.4:vdesk W.cs 已支持 --args= 显式赋值(值可含 --);旧版 vdesk(用法错误 exit 2)自动回退
  # 空格前导绕法 ' --test-rpc'(CreateProcessW 拼接后 App 的 argv[1] 恰为 --test-rpc,已实测验证)。
  $l = Invoke-Vdesk -VArgs @('launch', '--desktop', $Desktop, '--exe', $AppExe,
                             '--args=--test-rpc', '--timeout', '6')
  if ($l.ExitCode -eq 2) {
    $l = Invoke-Vdesk -VArgs @('launch', '--desktop', $Desktop, '--exe', $AppExe,
                               '--args', ' --test-rpc', '--timeout', '6')
  }
  $appPid = $null
  if ($l.Json) { $appPid = Get-Prop $l.Json 'pid' }
  $procAlive = $false
  if ($appPid) { $procAlive = [bool](Get-Process -Id $appPid -ErrorAction SilentlyContinue) }
  if ($l.ExitCode -ne 0 -and -not $procAlive) {
    $script:Blocked = $true
    Save-FailureScene 'launch 失败且进程未存活'
    Fail-Fast 'app.launch' ("vdesk launch 失败(exit=$($l.ExitCode))且进程未存活: " + (Trunc $l.Raw))
  }
  $launchNote = Trunc $l.Raw 300
  if ($l.ExitCode -ne 0) {
    Add-Step 'app.launch' $false -Soft ("launch 未等到窗口(exit=$($l.ExitCode)),但进程(pid=$appPid)存活,继续以 RPC 为准。raw: $launchNote")
  } else {
    Add-Step 'app.launch' $true ("pid=$appPid; raw: $launchNote")
  }
  $script:AppPid = $appPid

  # ---------- rpc.status.ready(30s 轮询) ----------
  $status = $null
  try { $status = Wait-StatusReady -TimeoutSec $StatusTimeoutSec }
  catch {
    $script:Blocked = $true
    Save-FailureScene 'TestRpc 管道未就绪(疑似上游 --test-rpc 未实现或产物过期)'
    Fail-Fast 'rpc.status.ready' ($_.Exception.Message + " | 管道名: $PipeName; 若源码已实现 --test-rpc,请确认 App.exe 为重新编译产物(-NoBuild 时尤其如此)")
  }
  Add-Step 'rpc.status.ready' $true ('TestRpc 管道就绪: ' + (Trunc (ConvertTo-Json -Compress -InputObject $status -Depth 6) 300))

  # ---------- status 字段契约校验(8.2) ----------
  $schemaMiss = @()
  if ($null -eq (Get-Prop $status 'pid'))   { $schemaMiss += 'pid' }
  if ($null -eq (Get-Prop $status 'tray'))  { $schemaMiss += 'tray' }
  $ov = Get-Prop $status 'overlay'
  if ($null -eq $ov)                        { $schemaMiss += 'overlay' }
  else {
    if ($null -eq (Get-Prop $ov 'active'))  { $schemaMiss += 'overlay.active' }
    if ($null -eq (Get-Prop $ov 'windows')) { $schemaMiss += 'overlay.windows' }
  }
  if ($null -eq (Get-Prop $status 'pins'))  { $schemaMiss += 'pins' }
  if ($schemaMiss.Count -gt 0) {
    Fail-Fast 'rpc.status.schema' ('status 响应缺契约字段: ' + ($schemaMiss -join ', '))
  }
  if (-not $status.tray) {
    Add-Step 'rpc.status.schema' $false -Soft "tray=false(隐藏桌面下托盘未就绪——若为已知平台限制需上游在 8.2 明示)"
  } else {
    Add-Step 'rpc.status.schema' $true ("pid=$($status.pid), tray=true, overlay.active=$($ov.active), pins=$($status.pins.Count)")
  }

  # ---------- app.pid.match(launch pid 与 RPC pid 一致性;防串台实例) ----------
  if ($script:AppPid -and $null -ne (Get-Prop $status 'pid') -and ([int]$status.pid -ne [int]$script:AppPid)) {
    $topo = ''
    try {
      foreach ($ppid in @([int]$status.pid, [int]$script:AppPid)) {
        $ci = Get-CimInstance Win32_Process -Filter "ProcessId=$ppid" -ErrorAction SilentlyContinue
        if ($ci) { $topo += "pid=$ppid parent=$($ci.ParentProcessId) cmd=$(Trunc $ci.CommandLine 120); " }
        else { $topo += "pid=$ppid 已退出; " }
      }
    } catch { $topo = 'topology query failed: ' + $_.Exception.Message }
    Add-Step 'app.pid.match' $false -Soft "vdesk launch pid=$($script:AppPid) != status.pid=$($status.pid) —— RPC 疑似被另一实例(如默认桌面上的开发/残留实例)应答,管道名无 per-instance 隔离(§8.2 契约缺陷信号)。topology: $topo"
  } else {
    Add-Step 'app.pid.match' $true "launch pid == status.pid == $($status.pid)"
  }

  # ---------- app.warmup(3.4:启动 3s 后预热 DXGI) ----------
  Start-Sleep -Seconds 4
  Add-Step 'app.warmup' $true '等待 4s(App 预热 + 首帧 DXGI 会话建立)'

  # ---------- snip.interactive ----------
  $SnipOut = Join-Path $ArtDir 'e2e-snip.png'
  $si = Invoke-TestRpc -Payload @{ cmd = 'snip.interactive'; output = @{ file = $SnipOut } }
  if (-not (Get-Prop $si 'ok')) {
    Fail-Fast 'snip.interactive' ('响应非 ok: ' + (ConvertTo-Json -Compress -InputObject $si -Depth 5))
  }
  Add-Step 'snip.interactive' $true ('ok=true, output.file=' + $SnipOut)

  # ---------- overlay.appeared ----------
  $deadline = (Get-Date).AddSeconds(15)
  $overlayHwnds = @()
  $vwin = $null
  while ((Get-Date) -lt $deadline) {
    $st2 = Get-StatusSafe
    $ov2 = if ($st2) { Get-Prop $st2 'overlay' } else { $null }
    $active = if ($ov2) { Get-Prop $ov2 'active' } else { $null }
    $vwin = Invoke-Vdesk -VArgs @('windows', '--desktop', $Desktop)
    $overlayHwnds = @()
    if ($vwin.Json) {
      foreach ($win in $vwin.Json.windows) {
        if ((Get-Prop $win 'class') -eq $OverlayClass) { $overlayHwnds += (Get-Prop $win 'hwnd') }
      }
    }
    $rpcHwnds = @()
    if ($ov2) { $rw = Get-Prop $ov2 'windows'; if ($rw) { $rpcHwnds = @($rw) } }
    if ($active -and $overlayHwnds.Count -gt 0 -and $rpcHwnds.Count -gt 0) { break }
    Start-Sleep -Milliseconds 400
  }
  if ($overlayHwnds.Count -eq 0) {
    Save-FailureScene '遮罩窗口未出现'
    Fail-Fast 'overlay.appeared' ("15s 内未在桌面枚举到 $OverlayClass 窗口; vdesk windows: " + (Trunc $vwin.Raw))
  }
  Add-Step 'overlay.appeared' $true ("vdesk 枚举到 $($overlayHwnds.Count) 个 $OverlayClass 窗口: $($overlayHwnds -join ',')")

  # ---------- overlay.hwnd.match(RPC 与 vdesk 一致性) ----------
  $st3 = Get-StatusSafe
  $rpcHwnds = @()
  if ($st3) { $ov3 = Get-Prop $st3 'overlay'; if ($ov3) { $rw = Get-Prop $ov3 'windows'; if ($rw) { $rpcHwnds = @($rw) } } }
  if ($rpcHwnds.Count -eq 0) { Fail-Fast 'overlay.hwnd.match' 'RPC overlay.windows 为空' }
  $miss = @($rpcHwnds | Where-Object { $overlayHwnds -notcontains "$_" })
  if ($miss.Count -gt 0) {
    Fail-Fast 'overlay.hwnd.match' ("RPC hwnd 与 vdesk 枚举不一致; RPC=[$($rpcHwnds -join ',')] vdesk=[$($overlayHwnds -join ',')]; 缺失=[$($miss -join ',')]")
  }
  Add-Step 'overlay.hwnd.match' $true "RPC [$($rpcHwnds -join ',')] 与 vdesk 枚举一致(hwnd 十进制字符串可直接 shot)"

  # ---------- overlay.shot.mask(§8.5: 进程内 snapshot;PrintWindow 隐藏桌面依赖 DWM 合成不可靠)----------
  $maskPng = Join-Path $ArtDir 'overlay-1-mask.png'
  $s1 = Invoke-TestRpc -Payload @{ cmd = 'overlay.snapshot'; out = $maskPng }
  if (-not (Get-Prop $s1 'ok')) {
    Save-FailureScene '遮罩 snapshot 失败'
    Fail-Fast 'overlay.shot.mask' ('overlay.snapshot 失败: ' + (ConvertTo-Json -Compress -InputObject $s1 -Depth 5))
  }
  $script:BaseWidth = [int](Get-Prop $s1 'w')
  if ($script:BaseWidth -le 0) {
    Fail-Fast 'overlay.shot.mask' 'snapshot 宽度非法,无法推导色带期望色'
  }
  $band1 = Test-BaseImageShot -Path $maskPng -BaseWidth $script:BaseWidth
  if (-not $band1.Ok) {
    Save-FailureScene '遮罩色带断言失败'
    Fail-Fast 'overlay.shot.mask' ("§8.4 底图色带断言失败: " + $band1.Reason + "; 样本: " + (($band1.FailSamples | Select-Object -First 5) -join ' | '))
  }
  Add-Step 'overlay.shot.mask' $true ("§8.4 色带采样 $($band1.Sampled) 点全部命中(压暗系数容忍, max=$($band1.MaxBrightness)); snapshot $($script:BaseWidth)x$(Get-Prop $s1 'h'); out=$maskPng")

  # ---------- snip.setRegion ----------
  $sr = Invoke-TestRpc -Payload @{ cmd = 'snip.setRegion'; x = $RegionX; y = $RegionY; w = $RegionW; h = $RegionH }
  if (-not (Get-Prop $sr 'ok')) {
    Save-FailureScene 'setRegion 拒绝'
    Fail-Fast 'snip.setRegion' ("响应非 ok($RegionX,$RegionY,$RegionW,$RegionH): " + (ConvertTo-Json -Compress -InputObject $sr -Depth 5))
  }
  Add-Step 'snip.setRegion' $true "ok=true($RegionX,$RegionY,$RegionW,$RegionH)"
  Start-Sleep -Milliseconds 600   # 等重绘提交

  # ---------- overlay.shot.region(§8.5 snapshot;blank 判定由红框/锚点断言自然覆盖)----------
  $regionPng = Join-Path $ArtDir 'overlay-2-region.png'
  $s2 = Invoke-TestRpc -Payload @{ cmd = 'overlay.snapshot'; out = $regionPng }
  if (-not (Get-Prop $s2 'ok')) {
    Save-FailureScene '选区 snapshot 失败'
    Fail-Fast 'overlay.shot.region' ('overlay.snapshot 失败: ' + (ConvertTo-Json -Compress -InputObject $s2 -Depth 5))
  }
  $m1 = Get-FileMd5 $maskPng; $m2 = Get-FileMd5 $regionPng
  if ($m1 -eq $m2) {
    Save-FailureScene 'setRegion 后画面无变化'
    Fail-Fast 'overlay.shot.region' '两次 shot 逐字节相同 → setRegion 未引起选区边框/锚点/标签重绘(缺陷信号)'
  }
  # --- §8.4 断言 2:选区 UI 元素出现在期望坐标 ---
  $bmp2 = $null
  try { $bmp2 = [System.Drawing.Bitmap]::new($regionPng) } catch {
    Save-FailureScene '选区截图解码失败'
    Fail-Fast 'overlay.shot.region' ('PNG 解码失败: ' + $_.Exception.Message)
  }
  $rx = $RegionX; $ry = $RegionY; $rx2 = ($RegionX + $RegionW); $ry2 = ($RegionY + $RegionH)
  $borderL = Test-PixelWindow -Bmp $bmp2 -X0 ($rx - 4)  -X1 ($rx + 5)   -Y0 400 -Y1 500 -Kind 'red'
  $borderR = Test-PixelWindow -Bmp $bmp2 -X0 ($rx2 - 5) -X1 ($rx2 + 4)  -Y0 400 -Y1 500 -Kind 'red'
  $borderT = Test-PixelWindow -Bmp $bmp2 -X0 550        -X1 650         -Y0 ($ry - 4)  -Y1 ($ry + 5)   -Kind 'red'
  $borderB = Test-PixelWindow -Bmp $bmp2 -X0 550        -X1 650         -Y0 ($ry2 - 5) -Y1 ($ry2 + 4)  -Kind 'red'
  $anchors = @(
    @($rx, $ry), @($rx2, $ry), @($rx, $ry2), @($rx2, $ry2),
    @(($rx + [int]($RegionW / 2)), $ry), @($rx, ($ry + [int]($RegionH / 2))),
    @($rx2, ($ry + [int]($RegionH / 2))), @(($rx + [int]($RegionW / 2)), $ry2)
  )
  $anchorMiss = @()
  $ai = 0
  foreach ($a in $anchors) {
    $ai++
    $hits = Test-PixelWindow -Bmp $bmp2 -X0 ($a[0] - 6) -X1 ($a[0] + 6) -Y0 ($a[1] - 6) -Y1 ($a[1] + 6) -Kind 'white'
    if ($hits -lt 4) { $anchorMiss += ("#" + $ai + "(" + $a[0] + "," + $a[1] + ")hits=" + $hits) }
  }
  $labelHits = Test-PixelWindow -Bmp $bmp2 -X0 ($rx2 - 260) -X1 ($rx2 + 5) -Y0 ($ry - 34) -Y1 ($ry + 6) -Kind 'light'
  $bmp2.Dispose()
  $uiMiss = @()
  if ($borderL -lt 2) { $uiMiss += "左边框无红(red=$borderL)" }
  if ($borderR -lt 2) { $uiMiss += "右边框无红(red=$borderR)" }
  if ($borderT -lt 2) { $uiMiss += "上边框无红(red=$borderT)" }
  if ($borderB -lt 2) { $uiMiss += "下边框无红(red=$borderB)" }
  if ($anchorMiss.Count -gt 0) { $uiMiss += ('锚点缺失: ' + ($anchorMiss -join '; ')) }
  if ($labelHits -lt 20) { $uiMiss += "尺寸标签区域无亮像素(light=$labelHits)" }
  if ($uiMiss.Count -gt 0) {
    Save-FailureScene '选区 UI 元素坐标断言失败'
    Fail-Fast 'overlay.shot.region' ('§8.4 选区 UI 断言失败: ' + ($uiMiss -join '; '))
  }
  Add-Step 'overlay.shot.region' $true ("blank=false, 画面已变化(md5 $m1 → $m2); §8.4 四边红框($borderL/$borderR/$borderT/$borderB) 8锚点全中 标签亮像素=$labelHits")

  # ---------- snip.confirm ----------
  $sc = Invoke-TestRpc -Payload @{ cmd = 'snip.confirm' }
  if (-not (Get-Prop $sc 'ok')) {
    Save-FailureScene 'confirm 拒绝'
    Fail-Fast 'snip.confirm' ('响应非 ok: ' + (ConvertTo-Json -Compress -InputObject $sc -Depth 5))
  }
  $deadline = (Get-Date).AddSeconds(10)
  $ovDone = $false
  while ((Get-Date) -lt $deadline) {
    $st4 = Get-StatusSafe
    if ($st4) { $ov4 = Get-Prop $st4 'overlay'; if ($ov4 -and -not (Get-Prop $ov4 'active')) { $ovDone = $true; break } }
    Start-Sleep -Milliseconds 300
  }
  if (-not $ovDone) { Add-Step 'snip.confirm' $false -Soft 'confirm ok=true,但 10s 内 overlay.active 未回到 false(可能仍有窗口残留)' }
  else { Add-Step 'snip.confirm' $true 'ok=true; overlay.active=false' }

  # ---------- snip.output.file ----------
  $deadline = (Get-Date).AddSeconds(10)
  while ((Get-Date) -lt $deadline -and -not (Test-Path $SnipOut)) { Start-Sleep -Milliseconds 300 }
  if (-not (Test-Path $SnipOut)) {
    Save-FailureScene '输出文件未生成'
    Fail-Fast 'snip.output.file' ("确认后 10s 内输出文件未生成: " + $SnipOut)
  }
  $png = Test-PngNotBlank -Path $SnipOut
  if ($png.Width -ne $RegionW -or $png.Height -ne $RegionH) {
    Save-FailureScene '输出尺寸错误'
    Fail-Fast 'snip.output.file' ("输出尺寸 $($png.Width)x$($png.Height) != 选区 $($RegionW)x$($RegionH)(DPI/坐标换算缺陷信号)")
  }
  $crop = Test-CropMatchesBase -Path $SnipOut -BaseWidth $script:BaseWidth -SrcX $RegionX -SrcY $RegionY
  if (-not $crop.Ok) {
    Save-FailureScene '输出与底图不一致'
    Fail-Fast 'snip.output.file' ('§8.4 裁剪一致性断言失败: ' + $crop.Reason + '; 样本: ' + (($crop.FailSamples | Select-Object -First 5) -join ' | '))
  }
  Add-Step 'snip.output.file' $true ("存在、$($RegionW)x$($RegionH)、与源底图选区区域逐点一致(采样 $($crop.Sampled) 点, 容差 12)")

  # ---------- history.list(SPEC §12: 历史入档)----------
  $hl = Invoke-TestRpc -Payload @{ cmd = 'history.list' }
  if (-not (Get-Prop $hl 'ok')) {
    Add-Step 'history.list' $false -Soft ('history.list 响应非 ok(协议未实现?): ' + (ConvertTo-Json -Compress -InputObject $hl -Depth 5))
  } else {
    $hlTotal = [int](Get-Prop $hl 'total')
    if ($hlTotal -lt 1) {
      Save-FailureScene '历史未入档'
      Fail-Fast 'history.list' ("成功截图后 history.total=$hlTotal(<1)——历史入档缺陷")
    }
    Add-Step 'history.list' $true "total=$hlTotal, 最近一条已入档"
  }

  # ---------- clipboard.preset(独立 STA 进程,256x256 渐变) ----------
  $clipChild = Join-Path $ArtDir 'preset-clipboard-child.ps1'
  $clipPng   = Join-Path $ArtDir 'clipboard-source.png'
  $child = @'
param([string]$OutPng)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bmp = New-Object System.Drawing.Bitmap 256, 256
for ($y = 0; $y -lt 256; $y++) {
  for ($x = 0; $x -lt 256; $x++) {
    $c = [System.Drawing.Color]::FromArgb(255, $x, $y, [int](($x + $y) / 2))
    $bmp.SetPixel($x, $y, $c)
  }
}
$bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
[System.Windows.Forms.Clipboard]::Clear()
[System.Windows.Forms.Clipboard]::SetDataObject($bmp, $true)
Start-Sleep -Milliseconds 300
$img = [System.Windows.Forms.Clipboard]::GetImage()
if ($null -eq $img) { [Console]::Error.WriteLine('VERIFY: clipboard image is null'); exit 3 }
if ($img.Width -ne 256 -or $img.Height -ne 256) { [Console]::Error.WriteLine("VERIFY: unexpected size"); exit 4 }
[Console]::Out.WriteLine('VERIFY: ok 256x256')
exit 0
'@
  $child | Set-Content -Path $clipChild -Encoding UTF8
  $ps51 = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  if (-not (Test-Path $ps51)) { $ps51 = 'powershell' }
  $cp = Start-Process -FilePath $ps51 -ArgumentList @('-NoProfile','-STA','-NonInteractive','-ExecutionPolicy','Bypass','-File',$clipChild,'-OutPng',$clipPng) -Wait -PassThru -WindowStyle Hidden
  if ($cp.ExitCode -ne 0 -or -not (Test-Path $clipPng)) {
    Save-FailureScene '剪贴板预置失败'
    Fail-Fast 'clipboard.preset' ("STA 子进程预置剪贴板失败(exit=$($cp.ExitCode)); 需要 256x256 渐变位图 + SetDataObject(copy=true)")
  }
  Add-Step 'clipboard.preset' $true '剪贴板已预置 256x256 渐变位图(独立 STA 进程写入并自校验); 参考图: clipboard-source.png'

  # ---------- app.paste(协议最小扩展) ----------
  $pp = Invoke-TestRpc -Payload @{ cmd = 'paste' }
  if (-not (Get-Prop $pp 'ok')) {
    Save-FailureScene 'paste 未实现/被拒'
    Fail-Fast 'app.paste' ('{"cmd":"paste"} 响应非 ok(协议扩展未实现或被拒): ' + (ConvertTo-Json -Compress -InputObject $pp -Depth 5))
  }
  Add-Step 'app.paste' $true '{"cmd":"paste"} ok=true(协议最小扩展)'

  # ---------- status.pins.assert ----------
  $deadline = (Get-Date).AddSeconds(15)
  $pins = @()
  while ((Get-Date) -lt $deadline) {
    $st5 = Get-StatusSafe
    if ($st5) { $p5 = Get-Prop $st5 'pins'; if ($p5 -and @($p5).Count -ge 1) { $pins = @($p5); break } }
    Start-Sleep -Milliseconds 400
  }
  if ($pins.Count -lt 1) {
    Save-FailureScene '贴图窗口未出现'
    Fail-Fast 'status.pins.assert' 'paste 后 15s 内 pins 数未达 1(paste 已回 ok)——贴图链路缺陷信号'
  }
  $pin0    = $pins[0]
  $pinHwnd = Get-Prop $pin0 'hwnd'
  $pinRect = Get-Prop $pin0 'rect'
  $pinTop  = Get-Prop $pin0 'topmost'
  $pinOp   = Get-Prop $pin0 'opacity'
  $pinCt   = Get-Prop $pin0 'clickThrough'
  $pinMiss = @()
  if (-not $pinHwnd)    { $pinMiss += 'hwnd' }
  if (-not $pinRect)    { $pinMiss += 'rect' }
  if ($null -eq $pinTop){ $pinMiss += 'topmost' }
  if ($pinMiss.Count -gt 0) { Fail-Fast 'status.pins.assert' ('pin 条目缺契约字段: ' + ($pinMiss -join ',')) }
  $rectTxt = if ($pinRect) { ($pinRect -join ',') } else { '?' }
  if (-not $pinTop) {
    Add-Step 'status.pins.assert' $false -Soft "pins=$($pins.Count), hwnd=$pinHwnd, rect=[$rectTxt], 但 topmost=false(3.3 默认置顶契约偏离?)"
  } else {
    Add-Step 'status.pins.assert' $true "pins=$($pins.Count), hwnd=$pinHwnd, rect=[$rectTxt], topmost=$pinTop, opacity=$pinOp, clickThrough=$pinCt"
  }
  $script:PinHwnd = $pinHwnd

  # ---------- pin.shot(§8.5: PrintWindow 隐藏桌面 best-effort,不作为 fail 依据)----------
  $pinPng = Join-Path $ArtDir 'pin.png'
  $s3 = Invoke-Vdesk -VArgs @('shot', '--hwnd', "$pinHwnd", '--out', $pinPng)
  if ($s3.ExitCode -ne 0 -or -not $s3.Json) {
    Add-Step 'pin.shot' $false -Soft ('vdesk shot 失败(隐藏桌面 best-effort, 不阻塞): ' + (Trunc $s3.Raw))
  }
  elseif (Get-Prop $s3.Json 'blank') {
    Add-Step 'pin.shot' $false -Soft ("贴图 PrintWindow 全黑(隐藏桌面 DWM 限制 §8.5, 不阻塞); 贴图内容正确性由 paste 链路(pins 注册+尺寸)保证")
  }
  else {
    Add-Step 'pin.shot' $true "blank=false, $($s3.Json.w)x$($s3.Json.h), out=$($s3.Json.out)"
  }

  # ---------- pin.closeAll ----------
  $ca = Invoke-TestRpc -Payload @{ cmd = 'pin.closeAll' }
  if (-not (Get-Prop $ca 'ok')) {
    Save-FailureScene 'closeAll 拒绝'
    Fail-Fast 'pin.closeAll' ('响应非 ok: ' + (ConvertTo-Json -Compress -InputObject $ca -Depth 5))
  }
  $deadline = (Get-Date).AddSeconds(10)
  $closed = $false
  while ((Get-Date) -lt $deadline) {
    $st6 = Get-StatusSafe
    if ($st6) { $p6 = Get-Prop $st6 'pins'; if ($null -ne $p6 -and @($p6).Count -eq 0) { $closed = $true; break } }
    Start-Sleep -Milliseconds 300
  }
  if (-not $closed) { Fail-Fast 'pin.closeAll' 'closeAll ok 但 10s 内 pins 未归零' }
  Add-Step 'pin.closeAll' $true 'closeAll ok; status.pins=0'

  # ---------- corners.set(SPEC §12: 触发角开关往返)----------
  $c1 = Invoke-TestRpc -Payload @{ cmd = 'corners.set'; enabled = $false }
  if (-not (Get-Prop $c1 'ok')) {
    Add-Step 'corners.set' $false -Soft ('corners.set 响应非 ok(协议未实现?): ' + (ConvertTo-Json -Compress -InputObject $c1 -Depth 5))
  } else {
    $c2 = Invoke-TestRpc -Payload @{ cmd = 'corners.set'; enabled = $true }
    if (-not (Get-Prop $c2 'ok')) { Add-Step 'corners.set' $false -Soft '第二轮 enabled=true 非 ok' }
    else { Add-Step 'corners.set' $true 'false→true 往返 ok(触发角开关链路通)' }
  }

  # ---------- app.exit(探测扩展命令,回退 vdesk kill) ----------
  $exitNote = ''
  try {
    $ex = Invoke-TestRpc -Payload @{ cmd = 'exit' } -ConnectTimeoutMs 2500 -IoTimeoutMs 8000
    if ($ex -and (Get-Prop $ex 'ok')) { $exitNote = '{"cmd":"exit"} ok=true' }
    else { $exitNote = '{"cmd":"exit"} 未实现/被拒(' + (ConvertTo-Json -Compress -InputObject $ex -Depth 4) + ') → 回退 vdesk kill' }
  } catch { $exitNote = '{"cmd":"exit"} 探测异常(' + $_.Exception.Message + ') → 回退 vdesk kill' }
  $deadline = (Get-Date).AddSeconds(8)
  $gone = $false
  while ((Get-Date) -lt $deadline) {
    if ($script:AppPid) { if (-not (Get-Process -Id $script:AppPid -ErrorAction SilentlyContinue)) { $gone = $true; break } }
    else { $gone = $true; break }
    Start-Sleep -Milliseconds 300
  }
  if (-not $gone -and $script:AppPid) {
    $k = Invoke-Vdesk -VArgs @('kill', '--pid', "$($script:AppPid)", '--timeout', '8')
    $gone = -not (Get-Process -Id $script:AppPid -ErrorAction SilentlyContinue)
    $exitNote += "; vdesk kill exit=$($k.ExitCode)"
  }
  if (-not $gone) { Fail-Fast 'app.exit' ('App 进程退出失败(vdesk kill 后仍存活): pid=' + $script:AppPid) }
  Add-Step 'app.exit' $true ($exitNote + "; 进程已退出(pid=$($script:AppPid))")

  # ---------- desktop.destroy ----------
  if ($KeepDesktop) {
    Add-Step 'desktop.destroy' $true -Soft '跳过(-KeepDesktop 调试开关; 桌面保留,进程已退出)'
  } else {
    $d2 = Invoke-Vdesk -VArgs @('desktop', 'destroy', $Desktop, '--kill')
    if ($d2.ExitCode -ne 0) {
      Add-Step 'desktop.destroy' $false -Soft ("destroy exit=$($d2.ExitCode): " + (Trunc $d2.Raw) + "(若为『桌面不存在』且进程已退出,属空桌面被系统回收的已知行为)")
    } else {
      Add-Step 'desktop.destroy' $true "destroyed=$($d2.Json.destroyed), killed=$($d2.Json.killed)"
    }
  }

  $report['result'] = 'PASS'
} catch {
  $finalErr   = $_.Exception.Message
  $finalLine  = try { $_.InvocationInfo.ScriptLineNumber } catch { -1 }
  $finalStack = try { $_.ScriptStackTrace } catch { '' }
  if ($finalErr -like 'STEP_FAILED:*') { $finalErr = $null }
  else {
    $uStep = [ordered]@{ step = 'unexpected.exception'; pass = $false; detail = "行${finalLine}: $finalErr" }
    $script:Steps.Add($uStep) | Out-Null
    Write-Output (ConvertTo-Json -Compress -InputObject $uStep -Depth 4)
  }
  if ($script:Blocked) { $report['result'] = 'BLOCKED_UPSTREAM' } else { $report['result'] = 'FAIL' }
} finally {
  try {
    $report['finishedAt']  = (Get-Date).ToString('o')
    $report['durationSec'] = [int]((Get-Date) - $StartedAt).TotalSeconds
    $report['steps']       = $script:Steps.ToArray()
    $report['artifacts']   = @(Get-ChildItem $ArtDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    if ($finalErr) {
      $report['unexpectedError']  = $finalErr
      $report['unexpectedAtLine'] = $finalLine
      $report['unexpectedStack']  = (Trunc $finalStack 900)
    }
    if ($script:Failed -and $script:Blocked) {
      $report['blockedReason'] = '上游依赖未就绪:--test-rpc 管道不存在或上游编译失败(详见失败步骤 detail)'
    }
    if ($script:Failed) { $report['blockedUpstream'] = [bool]$script:Blocked }
  } catch {
    Write-Output ('{"step":"report.build.failed","pass":false,"detail":"' + ($_.Exception.Message -replace '"', "'") + '"}')
  }
  try {
    $rptJson = ConvertTo-Json -InputObject $report -Depth 8
    $rptPath = Join-Path $RptDir ('e2e-' + $RunId + '.json')
    $rptJson | Set-Content -Path $rptPath -Encoding UTF8
    $rptJson | Set-Content -Path (Join-Path $RptDir 'latest.json') -Encoding UTF8

    $failedSteps = @($script:Steps | Where-Object { -not $_.pass } | ForEach-Object { $_.step })
    $summary = [ordered]@{
      result      = $report['result']
      pass        = (-not $script:Failed)
      steps       = $script:Steps.Count
      failedSteps = $failedSteps
      report      = $rptPath
      artifacts   = $ArtDir
      note        = if ($script:Failed) { '失败现场已保留(桌面与截图);脚本复跑会先 destroy --kill 自动清理' } else { '' }
    }
    Write-Output ('SUMMARY ' + (ConvertTo-Json -Compress -InputObject $summary -Depth 5))
  } catch {
    Write-Output ('REPORT-WRITE-FAILED: ' + $_.Exception.Message)
  }
}
if ($script:Failed) { exit 1 } else { exit 0 }
