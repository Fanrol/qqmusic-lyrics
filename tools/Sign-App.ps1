#Requires -Version 5.1
<#
.SYNOPSIS
    为本机编译出来的 exe 做自签名，并把这枚证书装进本机的「受信任的根证书颁发机构」与
    「受信任的发布者」，让 Windows 认得出发布者（消除"未知发布者"这类提示）。

.DESCRIPTION
    为什么需要这个：本项目没有买商业代码签名证书（OV/EV），所以编译出来的 exe 是
    "未签名 + 零信誉"，Windows 会把它当成"无法识别的应用"。

    本脚本做三件事：
      1. 在当前用户证书库（CurrentUser\My）里创建一枚自签的**代码签名**证书（没有才创建，有就复用）；
      2. 把它的**公钥**装进本机的 Root（受信任的根）和 TrustedPublisher（受信任的发布者）——
         只有这样签名链才在该机器上"可信"，UAC 才会显示发布者名字；
      3. 用 signtool 给 exe 签名，然后校验签名状态。

    重要限制：
      * **只对这台机器有效。** 别的电脑没有这枚根证书，签名链不可信，仍会报同样的问题。
      * **每次重新编译后都要重新签名**（exe 变了，旧签名就没了）。
      * 证书有效期 5 年；过期后需要重新生成并重签。

.PARAMETER ExePath
    要签名的 exe，默认为 Release 输出。

.PARAMETER Subject
    证书主题。UAC / 文件属性里显示的"发布者"就是这里 CN 的值。

.PARAMETER Recreate
    强制删掉旧证书并重新生成（换名字或想重置时用）。

.PARAMETER Remove
    卸载：把证书从本机 Root、TrustedPublisher 以及当前用户 My 里移除，然后退出。

.EXAMPLE
    # 生成证书 + 安装信任 + 签名当前 Release 版
    .\tools\Sign-App.ps1

.EXAMPLE
    # 换一个发布者名字重新来一遍
    .\tools\Sign-App.ps1 -Subject "CN=Beihe" -Recreate

.EXAMPLE
    # 反悔：把信任撤掉
    .\tools\Sign-App.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [string]$Subject = 'CN=Fanrol',
    [switch]$Recreate,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $RepoRoot 'src\QQMusicLyrics\bin\Release\net8.0-windows10.0.19041.0\QQMusicLyrics.exe'
}

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }

# ---------------- 前置检查 ----------------

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '需要管理员权限：要把证书装进本机证书库（LocalMachine）必须提权。请用"以管理员身份运行"的 PowerShell 执行本脚本。'
}

function Find-SignTool {
    $candidates = @()
    foreach ($root in @(
            'C:\Program Files (x86)\Windows Kits\10\bin',
            'C:\Program Files\Windows Kits\10\bin')) {
        if (-not (Test-Path $root)) { continue }
        $candidates += Get-ChildItem $root -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending
    }
    if ($candidates.Count -gt 0) { return $candidates[0].FullName }
    return $null
}

function Get-TrustedStore([string]$name) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($name, 'LocalMachine')
    $store.Open('ReadWrite')
    return $store
}

function Get-PublicOnly([System.Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
    # 只取公钥部分：装进本机 Root / TrustedPublisher 时不需要（也不该带）私钥。
    # 注意必须用 @(, $bytes) 包一层，否则 New-Object 会把 byte[] 当成一个个参数拆开。
    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList @(, $cert.RawData)
}

# ---------------- 卸载 ----------------

if ($Remove) {
    Write-Step "从本机移除自签名证书（$Subject）"
    $targets = @()

    foreach ($storeName in @('Root', 'TrustedPublisher')) {
        $store = Get-TrustedStore $storeName
        try {
            $found = @($store.Certificates | Where-Object { $_.Subject -eq $Subject })
            foreach ($c in $found) { $store.Remove($c) }
            Write-Ok ("$storeName : 移除 " + $found.Count + " 枚")
        } finally { $store.Close() }
    }

    $mine = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject })
    foreach ($c in $mine) {
        Remove-Item ("Cert:\CurrentUser\My\" + $c.Thumbprint) -Force
    }
    Write-Ok ("CurrentUser\My : 移除 " + $mine.Count + " 枚")

    Write-Note '（exe 上已有的签名不会消失，但签名链不再可信，Windows 会重新当成未知发布者）'
    return
}

# ---------------- 1. 取或建证书 ----------------

Write-Step "准备自签名代码签名证书（$Subject）"

$cert = $null
if (-not $Recreate) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($Recreate -and $cert) {
    Remove-Item ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force
    $cert = $null
}

if ($cert) {
    Write-Ok ("复用已有证书  指纹=" + $cert.Thumbprint + "  有效期至 " + $cert.NotAfter.ToString('yyyy-MM-dd'))
} else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5)

    Write-Ok ("已创建新证书  指纹=" + $cert.Thumbprint)
    Write-Note ("主题=" + $cert.Subject + "  有效期至 " + $cert.NotAfter.ToString('yyyy-MM-dd'))
}

# ---------------- 2. 装进本机受信任存储 ----------------

Write-Step '安装到本机证书库（Root + TrustedPublisher）'
$publicCert = Get-PublicOnly $cert

foreach ($storeName in @('Root', 'TrustedPublisher')) {
    $store = Get-TrustedStore $storeName
    try {
        $exists = @($store.Certificates | Where-Object { $_.Thumbprint -eq $publicCert.Thumbprint }).Count -gt 0
        if ($exists) {
            Write-Ok ("$storeName : 已存在，跳过")
        } else {
            $store.Add($publicCert)
            Write-Ok ("$storeName : 已添加")
        }
    } finally { $store.Close() }
}

# ---------------- 3. 签名 ----------------

Write-Step '给 exe 签名'

if (-not (Test-Path $ExePath)) {
    throw "找不到 exe：$ExePath（先编译：dotnet build QQMusicLyrics.sln -c Release）"
}
$ExePath = (Resolve-Path $ExePath).Path

$running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExePath)) -ErrorAction SilentlyContinue
if ($running) {
    throw ("程序正在运行（PID " + ($running.Id -join ',') + "），exe 被占用，请先退出再签。")
}

$signTool = Find-SignTool
if ($signTool) {
    Write-Note ("使用 signtool: " + $signTool)

    # 先带时间戳签，时间戳服务器不可达时退回不带时间戳（本机自用无差别）
    $signed = $false
    try {
        & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint /d 'QQ音乐浮窗歌词' `
            /td SHA256 /tr 'http://timestamp.digicert.com' $ExePath 2>&1 | ForEach-Object { Write-Note $_ }
        $signed = $LASTEXITCODE -eq 0
    } catch {
        $signed = $false
    }

    if (-not $signed) {
        Write-Note '带时间戳签名失败，改为不带时间戳重签'
        & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint /d 'QQ音乐浮窗歌词' $ExePath 2>&1 |
            ForEach-Object { Write-Note $_ }
        if ($LASTEXITCODE -ne 0) { throw 'signtool 签名失败' }
    }
} else {
    Write-Note '没找到 signtool，改用 PowerShell 的 Set-AuthenticodeSignature'
    $result = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert `
        -HashAlgorithm SHA256 -Description 'QQ音乐浮窗歌词'
    if (-not $result -or $result.Status -eq 'UnknownError') { throw 'Set-AuthenticodeSignature 签名失败' }
}
Write-Ok '签名完成'

# ---------------- 4. 校验 ----------------

Write-Step '校验签名'
$sig = Get-AuthenticodeSignature $ExePath
Write-Ok ("状态: " + $sig.Status)
Write-Ok ("发布者: " + $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '（无）' }))
if ($sig.Status -ne 'Valid') {
    Write-Host '    注意：状态不是 Valid，签名链没被信任，请检查证书是否成功装进 Root / TrustedPublisher。' -ForegroundColor Yellow
}

if ($signTool) {
    & $signTool verify /pa /q $ExePath 2>&1 | Out-Null
    Write-Ok ("signtool verify /pa: " + $(if ($LASTEXITCODE -eq 0) { '通过' } else { '未通过' }))
}

Write-Host ''
Write-Host '完成。提醒：' -ForegroundColor Yellow
Write-Host '  * 只对这台机器有效（别的电脑没有这枚根证书）。' -ForegroundColor DarkGray
Write-Host '  * 每次重新编译后 exe 会变，需要重新执行本脚本再签一次。' -ForegroundColor DarkGray
Write-Host '  * 想撤销信任：.\tools\Sign-App.ps1 -Remove' -ForegroundColor DarkGray
