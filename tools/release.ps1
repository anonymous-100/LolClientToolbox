# LOL 客户端工具箱 · 发布脚本
#
# 一条命令完成：编译 → 发布 → 装配插件 → 打包 → 打 tag。
#
# 用法（在仓库根目录，或任意位置传 -Repo）：
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 0.3.0
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -OutDir .dev -NoTag   # 只出开发迭代包
#
# 产物纪律（见 README「仓库与发布流程」）：
#   .dev/  开发迭代（未验证）→ 用 -OutDir .dev
#   dist/  已验证版本       → 默认；必须用户确认过才会写
#   GitHub Release 只在 -Upload 时创建（需要 gh CLI）
#
# 本脚本不做验证。进 dist 的前提是用户已确认这版能用 —— 门禁在流程里，不在脚本里。

param(
    # 版本号 = dist 包名 + git tag 名
    [string]$Version = "",
    # 输出目录：默认 dist（发布包），开发迭代用 .dev
    [string]$OutDir = "dist",
    # 不打 tag / 不推送（开发迭代用）
    [switch]$NoTag,
    # 同时把 tag 推到 GitHub（默认只打本地 tag，推送由你自己 git push --tags）
    [switch]$PushTags,
    # 附带创建 GitHub Release（需要 gh CLI，且必须给 -Version）
    [switch]$Upload,
    # 仓库根目录，缺省 = 脚本所在目录的上一级
    [string]$Repo = ""
)

$ErrorActionPreference = "Stop"
if ($Repo -eq "") {
    $Repo = Split-Path -Parent $PSScriptRoot   # tools/ 的上一级
}
Set-Location $Repo

function Step($msg) { Write-Host "== $msg" -ForegroundColor Cyan }
function Die($msg)  { Write-Host "✕ $msg" -ForegroundColor Red; exit 1 }

$pluginsRoot = "src\Plugins"

# ---------- 0. 参数检查 ----------
if ($OutDir -eq "dist" -and $Version -eq "") {
    Die "往 dist 发布必须给 -Version（它是包名和 tag 名）。开发迭代请用 -OutDir .dev -NoTag"
}
if ($Upload -and $Version -eq "") { Die "-Upload 需要 -Version" }
if ($Upload -and -not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Die "-Upload 需要 GitHub CLI（gh）。没装就先 git push --tags 然后到网页上发 Release"
}

# ---------- 1. 编译（零错误零警告是提交门禁，这里再确认一遍） ----------
Step "编译"
# 注意：PS5.1 里「2>&1 + EAP=Stop」会把 dotnet 的每行 stderr 变成异常，这里临时放宽
$ErrorActionPreference = "Continue"
dotnet build -c Release 2>&1 | Tee-Object -Variable buildLog | Out-Null
$buildExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($buildExit -ne 0) { Die "编译失败" }
$warns = ($buildLog | Select-String "(warning|警告) CS").Count
if ($warns -gt 0) { Die "编译有 $warns 条警告 —— 先清干净再发布（README 门禁）" }
Write-Host "  编译通过，0 警告"

# ---------- 2. 发布宿主 ----------
$outPath = $OutDir
Step "发布宿主 → $outPath"
dotnet publish src/LolClientToolbox/LolClientToolbox.csproj -c Release -o $outPath
if ($LASTEXITCODE -ne 0) { Die "publish 失败（目录被运行中的实例占用？先关掉工具箱）" }

# ---------- 3. 装配插件 ----------
# 关键点（都是踩过的）：
#   · 拷插件输出目录的【全部 dll】—— WinRT 投影默认不复制，缺了运行时才炸
#   · 但 LolClientToolbox.Contracts.dll 不能拷 —— 宿主已有一份，两份会类型冲突
#   · 按 mtime 挑【最新】的 TFM 目录 —— 历史构建会留下旧 TFM，不挑会拷错
#   · WinRT 运行时（SDK.NET/WinRT.Runtime）是进程级单例，多插件带多份会炸 —— 宿主会预载，部署时照常各带一份
Step "装配插件"
$manifests = Get-ChildItem $pluginsRoot -Directory | ForEach-Object {
    $mf = Join-Path $_.FullName "plugin.json"
    if (Test-Path $mf) { Get-Content $mf -Raw -Encoding UTF8 | ConvertFrom-Json }
}
foreach ($mf in $manifests) {
    # 插件目录名 = 仓库里的文件夹名；按「有 plugin.json 且 id 匹配」的目录反查
    $projDir = Get-ChildItem $pluginsRoot -Directory | Where-Object {
        (Test-Path (Join-Path $_.FullName "plugin.json")) -and
        ((Get-Content (Join-Path $_.FullName "plugin.json") -Raw -Encoding UTF8 | ConvertFrom-Json).id -eq $mf.id)
    }
    if (-not $projDir) { Die "找不到插件 $($mf.id) 的源目录" }

    $binRoot = Join-Path $projDir.FullName "bin\Release"
    $tfmDir = Get-ChildItem $binRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $tfmDir) { Die "$($mf.id) 没有编译输出（bin\Release）" }

    $dst = Join-Path $outPath "plugins\$($mf.id)"
    New-Item $dst -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $tfmDir.FullName "*.dll") $dst -Force
    Remove-Item (Join-Path $dst "LolClientToolbox.Contracts.dll") -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $projDir.FullName "plugin.json") $dst -Force
    if (Test-Path (Join-Path $projDir.FullName "说明.md")) {
        Copy-Item (Join-Path $projDir.FullName "说明.md") $dst -Force
    }
    Write-Host ("  {0,-14} ← {1}（{2} 个 dll）" -f $mf.id, $tfmDir.Name,
        (Get-ChildItem $dst -Filter *.dll).Count)
}

# ---------- 4. 清理发布包（开发目录不用清，那里本来就是杂物间） ----------
if ($outPath -eq "dist") {
    Step "清理发布包（剔除设置/日志/调试文件）"
    $drop = @("settings.json", "采样日志.txt", "*.pdb", "*.png", "config.json", "状态快照_*.txt")
    foreach ($pat in $drop) {
        Get-ChildItem $outPath -Recurse -Filter $pat -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    # 插件的私有设置不进包 —— 每个用户第一次启动自己生成
    Get-ChildItem "$outPath\plugins" -Recurse -Filter "settings.json" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    # 保障启动方式：普通权限起不来时用这个（见交接文档「UIPI」节）
    $bat = Join-Path $outPath "以管理员身份启动.bat"
    if (-not (Test-Path $bat)) {
        Set-Content -Path $bat -Encoding ASCII -Value @'
@echo off
taskkill /f /im LOLToolbox.exe >nul 2>&1
powershell -Command "Start-Process -FilePath '%~dp0LOLToolbox.exe' -Verb RunAs"
'@
        Write-Host "  已生成 以管理员身份启动.bat"
    }
}

# ---------- 5. 自测（不弹窗） ----------
Step "自测"
$exe = Join-Path $outPath "LOLToolbox.exe"
if (-not (Test-Path $exe)) { Die "发布产物里没有 LOLToolbox.exe" }
$shot = Join-Path $env:TEMP ("toolbox-selftest-{0}.png" -f (Get-Date -Format "HHmmss"))
& $exe --selftest $shot | Out-Null
Start-Sleep -Seconds 2
if (Test-Path $shot) {
    Write-Host "  主窗自测截图：$shot"
} else {
    Die "自测没产出截图 —— 界面初始化可能有问题"
}
$pluginCount = (Get-ChildItem $outPath\plugins -Directory).Count
Write-Host "  plugins/ 里 $pluginCount 个插件（加载是否正常看 $outPath\采样日志.txt 的「已加载 N 个功能插件」）"

# ---------- 6. 打 tag ----------
if (-not $NoTag -and $Version -ne "") {
    Step "打 tag v$Version"
    git tag -f "v$Version" | Out-Null
    if ($LASTEXITCODE -ne 0) { Die "打 tag 失败（已有同名 tag？确认要覆盖用 git tag -f v$Version）" }
    if ($PushTags) {
        git push origin ("refs/tags/v{0}" -f $Version)
        if ($LASTEXITCODE -ne 0) { Die "tag 推送失败" }
    } else {
        Write-Host "  本地 tag 已建。推送：git push --tags"
    }
}

# ---------- 7. 打包 zip + GitHub Release ----------
if ($Version -ne "" -and $outPath -eq "dist") {
    Step "打包 zip"
    $zip = Join-Path $env:TEMP ("LOLToolbox-v{0}.zip" -f $Version)
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outPath "*") -DestinationPath $zip
    Write-Host "  $zip（$([math]::Round((Get-Item $zip).Length/1MB,1)) MB）"

    if ($Upload) {
        Step "创建 GitHub Release v$Version"
        $notes = Join-Path $env:TEMP "release-notes.md"
        Set-Content -Path $notes -Encoding UTF8 -Value "LOL 客户端工具箱 v$Version`n`n详见仓库 README 与各插件的说明.md。"
        gh release create "v$Version" $zip --title "v$Version" --notes-file $notes
        if ($LASTEXITCODE -ne 0) { Die "Release 创建失败（gh 已登录？token 过期？）" }
    }
}

Write-Host ""
Write-Host "✅ 完成。产物：$outPath" -ForegroundColor Green
if ($outPath -eq "dist") {
    Write-Host "   提醒：进 dist 的前提是用户确认过这版没问题 —— 门禁在流程里，脚本替你执行不替你验证。"
}
