$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root '.secrets'
Push-Location -LiteralPath $root
try {
    foreach ($file in @('.secrets/tq_username', '.secrets/tq_password')) {
        git check-ignore --quiet -- $file
        if ($LASTEXITCODE -ne 0) {
            throw '无法确认凭据路径受 Git 忽略规则保护。'
        }
    }
}
finally {
    Pop-Location
}

$username = Read-Host '天勤账号' -MaskInput
$password = Read-Host '天勤密码' -MaskInput
if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrEmpty($password)) {
    throw '天勤账号和密码不能为空。'
}

[IO.Directory]::CreateDirectory($directory) | Out-Null
[IO.File]::WriteAllText((Join-Path $directory 'tq_username'), $username)
[IO.File]::WriteAllText((Join-Path $directory 'tq_password'), $password)
Write-Output '已保存本地天勤凭据。'
