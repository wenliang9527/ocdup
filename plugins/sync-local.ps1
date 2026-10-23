<#
.SYNOPSIS
    把仓库里的 ocdup-zcode 插件同步进 ZCode 的三层插件缓存，免去「卸载 → 重装」。

.DESCRIPTION
    ZCode 的插件安装有三层副本，缺一层就会出现「仓库改了但界面还是旧版」：

      1) 源目录      <repo>\plugins\ocdup-zcode              （git 里的真源）
      2) 市场存储    ~\.zcode\cli\plugins\marketplaces\<market>\<plugin>\   （Refresh 的产物）
      3) 运行缓存    ~\.zcode\cli\plugins\cache\<market>\<plugin>\<version>\ （会话实际加载的）
         外加 ~\.zcode\cli\plugins\installed_plugins.json 里的 version + installPath

    本脚本把 1 → 2 和 1 → 3 都同步，并更新安装记录，然后校验。
    之后只要**重启 ZCode**（不必卸载重装）就会加载新版本。

.PARAMETER RepoRoot
    Ocdup 仓库根目录。默认取本脚本所在目录的上一级。

.PARAMETER Marketplace
    本地市场 id。默认 ocdup-local。

.PARAMETER NoBackup
    跳过旧缓存目录的备份（默认会留一份 .bak-<时间戳>，失败可回滚）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File plugins\sync-local.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Marketplace = 'ocdup-local',
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'

# 默认仓库根 = 本脚本所在 plugins\ 的上一级（param 默认值里 $MyInvocation 不可用，只能体内算）
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Read-Json($path) {
    [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}
function Write-Json($obj, $path) {
    [System.IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 30), $Utf8NoBom)
}
function Say($msg) { Write-Host "  $msg" }
function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

# ── 0. 定位 ──
$pluginName = 'ocdup-zcode'
$src = Join-Path $RepoRoot "plugins\$pluginName"
$manifestPath = Join-Path $src '.zcode-plugin\plugin.json'
if (-not (Test-Path $manifestPath)) { throw "找不到插件清单：$manifestPath（-RepoRoot 指对了吗？）" }

$version = (Read-Json $manifestPath).version
if (-not $version) { throw "plugin.json 里没有 version" }

$zcodePlugins = Join-Path $env:USERPROFILE '.zcode\cli\plugins'
if (-not (Test-Path $zcodePlugins)) { throw "找不到 ZCode 插件目录：$zcodePlugins" }

$storeDir = Join-Path $zcodePlugins "marketplaces\$Marketplace"
$cacheDir = Join-Path $zcodePlugins "cache\$Marketplace\$pluginName\$version"
$installedJson = Join-Path $zcodePlugins 'installed_plugins.json'
$knownJson = Join-Path $zcodePlugins 'known_marketplaces.json'

Step "同步 $pluginName@$version"
Say "源目录   : $src"
Say "市场存储 : $storeDir\$pluginName"
Say "运行缓存 : $cacheDir"

# ── 1. 运行缓存：cache\<market>\<plugin>\<version>\ ──
Step "1/4 运行缓存"
if (Test-Path $cacheDir) {
    if (-not $NoBackup) {
        $bak = "$cacheDir.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Move-Item $cacheDir $bak
        Say "旧缓存已备份 → $(Split-Path -Leaf $bak)"
    } else {
        Remove-Item $cacheDir -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
# node_modules 体积大且是构建期依赖，但面板 server 运行时确实要它 —— 一起带过去
robocopy $src $cacheDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制到运行缓存失败（robocopy exit $LASTEXITCODE）" }
Say "已复制 $(@(Get-ChildItem $cacheDir -Recurse -File).Count) 个文件"

# ── 2. 市场存储：marketplaces\<market>\<plugin>\ ──
Step "2/4 市场存储（Refresh 的产物）"
$storePlugin = Join-Path $storeDir $pluginName
if (Test-Path $storePlugin) { Remove-Item $storePlugin -Recurse -Force }
New-Item -ItemType Directory -Path $storePlugin -Force | Out-Null
robocopy $src $storePlugin /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制到市场存储失败（robocopy exit $LASTEXITCODE）" }
Say "已复制到 $storePlugin"

# 市场清单里的版本号也要同步，否则界面仍显示旧版
$storeManifest = Join-Path $storeDir 'marketplace.json'
if (Test-Path $storeManifest) {
    $m = Read-Json $storeManifest
    foreach ($p in $m.plugins) { if ($p.name -eq $pluginName) { $p.version = $version } }
    Write-Json $m $storeManifest
    Say "marketplace.json → version $version"
} else {
    Write-Warning "市场清单不存在：$storeManifest（市场未注册过？先在界面里添加本地市场）"
}

# ── 3. 安装记录 ──
Step "3/4 安装记录 installed_plugins.json"
if (-not (Test-Path $installedJson)) { throw "找不到 $installedJson" }
Copy-Item $installedJson "$installedJson.bak" -Force
$inst = Read-Json $installedJson
$found = $false
foreach ($p in $inst.plugins) {
    if ($p.name -eq $pluginName) {
        $p.version = $version
        $p.installPath = $cacheDir
        $p.updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        $found = $true
    }
}
if (-not $found) { throw "installed_plugins.json 里没有 $pluginName —— 这个插件还没装过，请先在界面里安装一次" }
Write-Json $inst $installedJson
Say "version=$version  installPath=$cacheDir"

# 市场记录时间戳也刷新一下（界面据此判断新鲜度）
if (Test-Path $knownJson) {
    $known = Read-Json $knownJson
    foreach ($mk in $known.marketplaces) {
        if ($mk.id -eq $Marketplace) {
            $mk.lastUpdated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        }
    }
    Write-Json $known $knownJson
}

# ── 4. 校验 ──
Step "4/4 校验"
$checks = @(
    @{ Name = '运行缓存 plugin.json 版本'; Path = (Join-Path $cacheDir '.zcode-plugin\plugin.json'); Expect = $version },
    @{ Name = '市场存储 plugin.json 版本'; Path = (Join-Path $storePlugin '.zcode-plugin\plugin.json'); Expect = $version }
)
$fail = $false
foreach ($c in $checks) {
    if (-not (Test-Path $c.Path)) { Write-Host "  [X] $($c.Name)：文件缺失" -ForegroundColor Red; $fail = $true; continue }
    $v = (Read-Json $c.Path).version
    if ($v -eq $c.Expect) { Say "[OK] $($c.Name) = $v" }
    else { Write-Host "  [X] $($c.Name) = $v（期望 $($c.Expect)）" -ForegroundColor Red; $fail = $true }
}

# 面板产物存在且含最新标记
$panelHtml = Join-Path $cacheDir 'panel\dist\panel.html'
if (Test-Path $panelHtml) {
    $kb = [math]::Round((Get-Item $panelHtml).Length / 1KB)
    Say "[OK] 面板产物 panel.html = ${kb} KB"
} else {
    Write-Host "  [X] 面板产物缺失：$panelHtml（先在 panel\ 里跑 npm run build）" -ForegroundColor Red
    $fail = $true
}

if ($fail) {
    Write-Host "`n同步失败。回滚：把 *.bak / *.bak-<时间戳> 换回原目录。" -ForegroundColor Red
    exit 1
}

Write-Host "`n完成。**完全退出 ZCode 再打开**即可加载 $version（不需要卸载重装）。" -ForegroundColor Green
