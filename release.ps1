<#
    ===================================================================
      AutoZip 发版脚本

      由 release.bat 调用。所有提示都是中文，交互全部走这里。

      用法：
        release.bat                     交互式发版
        release.bat 6.6.7               直接给出版本号
        release.bat 6.6.7 -DryRun       只说会做什么，一个字节都不改
        release.bat 6.6.7 -NoSelfTest   本地关卡里跳过界面自检
        release.bat 6.6.7 -NoGate       本机完全不编译，直接推（CI 那边照样跑测试）
        release.bat -ShowGitOutput      把 git 的原始输出也打出来（排错用）
        release.bat -FirstCommit         强制走首次提交流程（通常由 release.bat 自动判断，一般不需手动加）
        版本号留空                       不改版本号，按当前 Directory.Build.props 发一版

      它做的事，按这个顺序：

        1  认出当前是「首次提交」还是「常规发版」，两者走不同的路
        2  常规发版：问版本号 -> 跑本地关卡（publish.cmd：单元测试 ->
           单文件发布 -> 在发布产物上跑界面自检）
        3  把版本号写进 Directory.Build.props 的 Version /
           AssemblyVersion / FileVersion 三处
        4  提交。提交说明一律由你手动输入，脚本不代写
        5  打 tag vX.Y.Z、推分支、推 tag
        6  GitHub Actions 接手，从 tag 重新编译并建 Release

      为什么逻辑在 .ps1 里而不是全塞进 .bat：

        cmd.exe 用控制台代码页（本机 936）解码批处理文件，不是 UTF-8。
        一行 UTF-8 中文的字节数若让末尾落成一个不成对的 GBK 前导字节，
        就会把行尾的回车一起吞掉，把下一条命令粘到注释上。所以 .bat
        里放不了 UTF-8 中文。PowerShell 读 UTF-8 带 BOM 的 .ps1 是妥
        当的，中文提示放在这里既可靠又不用赌字节奇偶。

        本文件必须保存成「UTF-8 带 BOM」。Windows PowerShell 5.1 读不
        带 BOM 的 .ps1 时按本地代码页解，中文会变乱码。
    ===================================================================
#>

param(
    # 位置 0：版本号。留空表示不改动版本号。
    [Parameter(Position = 0)]
    [string]$Version = '',

    [switch]$DryRun,
    [switch]$NoSelfTest,
    [switch]$NoGate,
    [switch]$ShowGitOutput,

    #  由 release.bat 自动判断后传入：强制走「首次提交」流程。
    #  release.bat 已经用 ASCII 安全的 git 命令认过一次，这里再强制一遍，
    #  确保首次提交这条路一定会被走到。
    [switch]$FirstCommit,

    # 兜住其余写法，比如老式的裸词 dryrun / nogate
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @()
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
#  参数归一化
#
#  老版本 release.bat 用的是裸词参数（dryrun / noselftest / nogate），
#  新写法是 -DryRun 这样的开关。两种都收下，别让老习惯报错。
# ---------------------------------------------------------------------
$script:DryRun        = [bool]$DryRun
$script:NoSelfTest    = [bool]$NoSelfTest
$script:NoGate        = [bool]$NoGate
$script:ShowGitOutput = [bool]$ShowGitOutput

$tokens = New-Object System.Collections.ArrayList
if ($Version -ne '') { [void]$tokens.Add($Version) }
foreach ($t in $Rest) {
    if ($null -ne $t -and $t -ne '') { [void]$tokens.Add($t) }
}

$Version = ''
foreach ($t in $tokens) {
    $low = $t.ToLowerInvariant()
    if ($low -eq 'dryrun' -or $low -eq 'dry-run') {
        $script:DryRun = $true
        continue
    }
    if ($low -eq 'noselftest' -or $low -eq 'no-self-test') {
        $script:NoSelfTest = $true
        continue
    }
    if ($low -eq 'nogate' -or $low -eq 'no-gate') {
        $script:NoGate = $true
        continue
    }
    if ($low -eq 'showgitoutput' -or $low -eq 'show-git-output') {
        $script:ShowGitOutput = $true
        continue
    }
    if ($t -match '^v?\d+\.\d+\.\d+$') {
        $Version = $t -replace '^v', ''
        continue
    }
    Write-Host ''
    Write-Host ('  看不懂这个参数：' + $t) -ForegroundColor Red
    Write-Host '  能用的只有版本号（形如 6.6.7）和这几个开关：-DryRun、-NoSelfTest、-NoGate、-ShowGitOutput。'
    Write-Host ''
    exit 1
}

# ---------------------------------------------------------------------
#  路径与常量
# ---------------------------------------------------------------------
$script:Repo          = $PSScriptRoot
$script:PropsFile     = Join-Path $script:Repo 'Directory.Build.props'
$script:PublishCmd    = Join-Path $script:Repo 'publish.cmd'
$script:GitIgnore     = Join-Path $script:Repo '.gitignore'
$script:DefaultRemote = 'https://github.com/ovoene/AutoZip.git'
$script:ActionsUrl    = 'https://github.com/ovoene/AutoZip/actions'
$script:ReleaseBase   = 'https://github.com/ovoene/AutoZip/releases/tag/'

# ---------------------------------------------------------------------
#  控制台编码
#
#  [Console]::OutputEncoding 决定写到控制台的字节怎么编码。本机默认是
#  936（GBK），中文碰巧能显示，但换一台非中文 Windows 就全是乱码。这里
#  显式改成 UTF-8，中文提示在哪台机器上都是对的。
#
#  但 cmd.exe 在代码页 65001 下处理批处理文件时有过丢文件读偏移的毛病，
#  而本地关卡要跑 publish.cmd。所以跑关卡之前先把编码还原，跑完再改回来。
#  脚本结束时也必须还原 —— 这个设置落在控制台宿主上，PowerShell 退出后
#  不会自己复原，不还原就会把用户的命令行留在 65001 上。
# ---------------------------------------------------------------------
$script:OldOutputEncoding = $null

function Set-ConsoleUtf8 {
    if ($null -ne $script:OldOutputEncoding) {
        try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
    }
}

function Restore-ConsoleEncoding {
    if ($null -ne $script:OldOutputEncoding) {
        try { [Console]::OutputEncoding = $script:OldOutputEncoding } catch { }
    }
}

try { $script:OldOutputEncoding = [Console]::OutputEncoding } catch { $script:OldOutputEncoding = $null }
Set-ConsoleUtf8

# ---------------------------------------------------------------------
#  输出
# ---------------------------------------------------------------------
function Write-Hr      { Write-Host ('=' * 68) -ForegroundColor DarkGray }
function Write-Blank   { Write-Host '' }
function Write-Info    { param([string]$Text) Write-Host ('  ' + $Text) }
function Write-Note    { param([string]$Text) Write-Host ('  · ' + $Text) -ForegroundColor DarkGray }
function Write-Step    { param([string]$Text) Write-Host ('  > ' + $Text) -ForegroundColor Cyan }
function Write-Good    { param([string]$Text) Write-Host ('  [完成] ' + $Text) -ForegroundColor Green }
function Write-WarnTip { param([string]$Text) Write-Host ('  [注意] ' + $Text) -ForegroundColor Yellow }

function Write-Head {
    param([string]$Text)
    Write-Blank
    Write-Hr
    Write-Host ('  ' + $Text) -ForegroundColor Cyan
    Write-Hr
}

# ---------------------------------------------------------------------
#  退出
# ---------------------------------------------------------------------
function Exit-Script {
    param([int]$Code = 0)
    Restore-ConsoleEncoding
    exit $Code
}

function Exit-Cancel {
    Write-Blank
    Write-WarnTip '已取消。没有改动任何东西。'
    Exit-Script 1
}

function Exit-Fail {
    param([string]$Text)
    Write-Blank
    Write-Host ('  [失败] ' + $Text) -ForegroundColor Red
    Write-Blank
    Write-Host '  发版已中止。' -ForegroundColor Red
    Exit-Script 1
}

# ---------------------------------------------------------------------
#  输入
# ---------------------------------------------------------------------
function Read-YesNo {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Default = 'y'
    )
    $tail = if ($Default -eq 'y') { '（y/是 或 n/否，直接回车表示是）' } else { '（y/是 或 n/否，直接回车表示否）' }
    while ($true) {
        $a = (Read-Host -Prompt ('  ' + $Prompt + ' ' + $tail)).Trim()
        if ($a -eq '') { return ($Default -eq 'y') }
        if ($a -match '^(y|yes|是|对|好|确认|确定)$') { return $true }
        if ($a -match '^(n|no|否|不|取消|算了)$')     { return $false }
        Write-Host '  没看懂。请输入 y/是 或 n/否。' -ForegroundColor Yellow
    }
}

function Read-Required {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [string]$Why = '这一项不能为空，必须由你手动输入。'
    )
    while ($true) {
        $a = (Read-Host -Prompt ('  ' + $Prompt)).Trim()
        if ($a -ne '') { return $a }
        Write-Host ('  ' + $Why) -ForegroundColor Yellow
    }
}

function Read-WithDefault {
    param(
        [Parameter(Mandatory = $true)][string]$Prompt,
        [Parameter(Mandatory = $true)][string]$Default
    )
    $a = (Read-Host -Prompt ('  ' + $Prompt + '（直接回车用 ' + $Default + '）')).Trim()
    if ($a -eq '') { return $Default }
    return $a
}

# ---------------------------------------------------------------------
#  提交说明：一律手动输入
#
#  不接受直接回车带过。空输入会一直问下去，因为提交说明是历史里唯一能
#  解释「这次为什么发」的地方，脚本代写出来的东西等于没写。
# ---------------------------------------------------------------------
function Read-CommitMessage {
    param(
        [Parameter(Mandatory = $true)][string]$Scene,
        [Parameter(Mandatory = $true)][string]$Suggest
    )
    Write-Blank
    Write-Host ('  ' + $Scene + '：提交说明需要你自己写。') -ForegroundColor Cyan
    Write-Note ('可以参考这个写法：' + $Suggest)
    Write-Note '脚本不会替你填，直接回车不会通过。'
    Write-Blank

    $subject = ''
    while ($true) {
        $subject = (Read-Host -Prompt '  提交说明（必填）').Trim()
        if ($subject -eq '') {
            Write-Host '  提交说明不能为空，请手动写一句话说明这次改了什么。' -ForegroundColor Yellow
            continue
        }
        if ($subject.Length -gt 120) {
            Write-Host '  太长了（超过 120 个字符）。先写一句，细节放到补充说明里。' -ForegroundColor Yellow
            continue
        }
        break
    }

    $body = (Read-Host -Prompt '  补充说明（可留空，直接回车跳过）').Trim()

    return @{ Subject = $subject; Body = $body }
}

# ---------------------------------------------------------------------
#  git 调用
#
#  默认不把 git 的英文原文打出来 —— 本脚本的提示一律中文。要看原文加
#  -ShowGitOutput。
# ---------------------------------------------------------------------
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)

    if ($null -eq $GitArgs -or $GitArgs.Count -eq 0) {
        throw 'Invoke-Git 收到了空的 git 参数。'
    }

    #  必须是 SilentlyContinue，不能用 Continue。
    #  2>&1 会把 git 那一行英文报错变成 PowerShell 的 NativeCommandError
    #  纪录；Continue 只是让它不中断，照样会把一整坨英文（fatal: ...、
    #  CategoryInfo、FullyQualifiedErrorId）喷到屏幕上。本脚本的提示必须
    #  是中文，所以这里连显示都不许显示 —— 想看原文就加 -ShowGitOutput。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $raw = & git @GitArgs 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    $lines = New-Object System.Collections.ArrayList
    if ($null -ne $raw) {
        foreach ($l in $raw) {
            if ($null -eq $l) { continue }
            [void]$lines.Add(($l | Out-String).TrimEnd("`r", "`n"))
        }
    }

    if ($script:ShowGitOutput -and $lines.Count -gt 0) {
        foreach ($l in $lines) { Write-Host ('      | ' + $l) -ForegroundColor DarkGray }
    }

    return [pscustomobject]@{ Code = [int]$code; Lines = $lines }
}

function Git-Ok {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $r = Invoke-Git @GitArgs
    return ($r.Code -eq 0)
}

function Git-First {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $r = Invoke-Git @GitArgs
    if ($r.Code -ne 0) { return '' }
    if ($r.Lines.Count -eq 0) { return '' }
    return [string]$r.Lines[0]
}

# ---------------------------------------------------------------------
#  本地关卡：publish.cmd
#
#  单元测试 -> 清理 -> 自包含单文件发布 -> 在发布产物上跑界面自检。
#  这一条过了，CI 上那条才是绿的。
# ---------------------------------------------------------------------
function Invoke-LocalGate {
    if (-not (Test-Path -LiteralPath $script:PublishCmd)) {
        Write-Blank
        Write-Host ('  [失败] 找不到 ' + $script:PublishCmd) -ForegroundColor Red
        return $false
    }

    $gateLabel = if ($script:NoSelfTest) { 'publish.cmd noselftest（跳过界面自检）' } else { 'publish.cmd（含界面自检）' }
    Write-Step ('正在跑本地关卡：' + $gateLabel)
    Write-Note '这一步要几分钟：单元测试 -> 清理 -> 单文件发布 -> 界面自检。'
    Write-Blank

    # 关键：先把控制台编码还原成系统默认值再交给 cmd.exe。
    # cmd.exe 在代码页 65001 下处理批处理文件有过丢文件读偏移的毛病，
    # 而 publish.cmd 里既有 goto 又有多行括号块，中招了就会诡异失败。
    Restore-ConsoleEncoding
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName  = 'cmd.exe'
        $psi.Arguments = '/c "' + $script:PublishCmd + '"' + $(if ($script:NoSelfTest) { ' noselftest' } else { '' })
        $psi.WorkingDirectory = $script:Repo
        $psi.UseShellExecute  = $false

        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.WaitForExit()
        $code = $proc.ExitCode
    }
    catch {
        Set-ConsoleUtf8
        Write-Blank
        Write-Host ('  [失败] 没能启动本地关卡：' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
    finally {
        Set-ConsoleUtf8
    }

    if ($code -ne 0) { return $false }

    Write-Good '本地关卡通过。'
    return $true
}

# =====================================================================
#  开工
# =====================================================================
Write-Head 'AutoZip 发版'

Write-Info ('项目目录：' + $script:Repo)
if ($script:DryRun)   { Write-WarnTip '演练模式：只会说明要做什么，不会写入、提交、打标签或推送。' }
if ($script:NoGate)   { Write-WarnTip '已指定 -NoGate：本机不做编译和测试，直接提交推送（CI 那边照样会跑测试）。' }
elseif ($script:NoSelfTest) { Write-Note '本地关卡会跳过界面自检。' }

# ---------------------------------------------------------------------
#  git 在不在
# ---------------------------------------------------------------------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Exit-Fail '本机没有找到 git。请先安装 Git for Windows，并确认它在 PATH 里。'
}

# =====================================================================
#  第一步：认出当前是首次提交还是常规发版
#
#  三种情况要分开看：
#    A  这个目录根本不是 git 仓库           -> 首次提交，先要 git init
#    B  是仓库，但 HEAD 指向的提交不存在    -> 首次提交（init 之后没提交过，
#                                              或者用 --orphan 建了空分支）
#    C  HEAD 存在                           -> 常规发版
#
#  原来是靠 git rev-parse --abbrev-ref HEAD 拿分支名，拿不到就报错退出。
#  在未出生的分支上它返回的是 "HEAD" 四个字母，于是被当成「不在 main 上」
#  反复询问，首次提交这条路根本走不通。这里改用 symbolic-ref，它在未出生
#  的分支上照样能给出真正的分支名。
# =====================================================================
Write-Head '第一步：检查仓库状态'

$isRepo  = ((Git-First 'rev-parse' '--is-inside-work-tree') -eq 'true')
$hasHead = (Git-Ok 'rev-parse' '-q' '--verify' 'HEAD')

$IsFirstCommit = $false

#  release.bat 已经在外部用 git 命令判断过一次。如果它显式传了 -FirstCommit，
#  这里直接采信，强制走首次提交流程（避免控制台代码页差异导致的判断偏差）。
if ($FirstCommit) {
    $IsFirstCommit = $true
    Write-Info '已通过 -FirstCommit 参数强制走首次提交流程。'
}

if (-not $isRepo) {
    $IsFirstCommit = $true
    Write-WarnTip '这个目录还不是 Git 仓库：'
    Write-Info ('    ' + $script:Repo)
    Write-Blank
    Write-Info '也就是说，接下来要做的是这个项目的第一次提交。'
    Write-Note '第一次提交和平时发版不一样：仓库要初始化、提交人要配置、'
    Write-Note '远端要挂上，而且所有文件都是新增的。下面一步一步来。'
    Write-Blank

    if (-not (Read-YesNo '现在把这个目录初始化成 Git 仓库？')) {
        Exit-Cancel
    }
}
elseif (-not $hasHead) {
    $IsFirstCommit = $true
    Write-WarnTip '这里已经是一个 Git 仓库，但还没有任何提交。'
    Write-Info '属于首次提交，走首次提交的流程。'
}
else {
    $headShort   = Git-First 'rev-parse' '--short' 'HEAD'
    $commitCount = Git-First 'rev-list' '--count' 'HEAD'
    Write-Info '这是一个已经有提交的仓库，走常规发版流程。'
    Write-Info ('当前 HEAD：' + $headShort + '，本分支共 ' + $commitCount + ' 个提交。')
}

# =====================================================================
#  首次提交
# =====================================================================
if ($IsFirstCommit) {

    # -------------------------------------------------------------------
    #  1.1 初始化仓库（只在还不是仓库的时候）
    # -------------------------------------------------------------------
    if (-not $isRepo) {
        Write-Blank
        $initBranch = Read-WithDefault '初始分支名' 'main'
        if ($initBranch -notmatch '^[A-Za-z0-9._/+-]+$') {
            Exit-Fail ('「' + $initBranch + '」不是合法的分支名。只能用字母、数字、点、下划线、加号、减号和斜杠。')
        }

        if ($script:DryRun) {
            Write-Step ('[演练] 会执行：git init -b ' + $initBranch)
        }
        else {
            Write-Step ('正在初始化仓库，初始分支为 ' + $initBranch + ' ...')
            $r = Invoke-Git 'init' '-b' $initBranch
            if ($r.Code -ne 0) {
                # git 2.28 之前没有 -b。退回老办法：先 init，再把 HEAD 指过去。
                $r2 = Invoke-Git 'init'
                if ($r2.Code -ne 0) { Exit-Fail '初始化 Git 仓库失败。' }
                $r3 = Invoke-Git 'symbolic-ref' 'HEAD' ('refs/heads/' + $initBranch)
                if ($r3.Code -ne 0) { Exit-Fail ('初始化成功，但没能切换到分支 ' + $initBranch + '。') }
            }
            Write-Good '仓库已初始化。'
            $isRepo = $true
        }
    }

    # -------------------------------------------------------------------
    #  1.2 当前分支名
    # -------------------------------------------------------------------
    $branch = Git-First 'symbolic-ref' '--short' '-q' 'HEAD'
    if ($branch -eq '') { $branch = 'main' }
    Write-Note ('当前分支：' + $branch)

    # -------------------------------------------------------------------
    #  1.3 提交人身份
    #
    #  没配 user.name / user.email 时 git 会直接拒绝提交，而且报的是一串
    #  英文。首次提交前先问清楚，写进仓库的本地配置里。
    # -------------------------------------------------------------------
    Write-Head '第二步：提交人身份'

    $cfgName  = Git-First 'config' '--get' 'user.name'
    $cfgEmail = Git-First 'config' '--get' 'user.email'

    if ($cfgName -ne '' -and $cfgEmail -ne '') {
        Write-Info ('已配置的提交人：' + $cfgName + ' <' + $cfgEmail + '>')
        if (-not (Read-YesNo '用这个身份提交？')) {
            $cfgName  = ''
            $cfgEmail = ''
        }
    }

    if ($cfgName -eq '' -or $cfgEmail -eq '') {
        Write-Blank
        Write-Info '还没有配置提交人。第一次提交必须先有这个，否则 git 会拒绝提交。'
        $newName  = Read-Required '提交人名字（例如 ovoene）'
        $newEmail = Read-Required '提交人邮箱（例如 ovoene@example.com）'
        if ($newEmail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
            Write-WarnTip '这个邮箱看着不太对，但还是按你输入的写进去了。想改就执行：git config user.email 你的邮箱'
        }
        if ($script:DryRun) {
            Write-Step ('[演练] 会写入 git config：' + $newName + ' <' + $newEmail + '>')
        }
        else {
            [void](Invoke-Git 'config' 'user.name'  $newName)
            [void](Invoke-Git 'config' 'user.email' $newEmail)
            Write-Good ('提交人已写入：' + $newName + ' <' + $newEmail + '>')
        }
        $cfgName  = $newName
        $cfgEmail = $newEmail
    }

    # -------------------------------------------------------------------
    #  1.4 .gitignore
    #
    #  没有它，bin/ obj/ publish/ 这些几百兆的构建产物会一起进第一次提交，
    #  而进了历史就再也清不干净。
    # -------------------------------------------------------------------
    Write-Head '第三步：忽略规则'

    if (Test-Path -LiteralPath $script:GitIgnore) {
        # 死守：AI 工作目录（项目记忆 / 对话日志）绝不允许进版本库。
        # 如果现成的 .gitignore 漏了它，这里自动补上，避免首次提交就把
        # 这些私密文件推到 GitHub。
        $giText = [IO.File]::ReadAllText($script:GitIgnore)
        if ($giText -notmatch '\.workbuddy-ai') {
            $giAppended = $giText.TrimEnd() + "`r`n`r`n# WorkBuddy AI 的工作目录（含项目记忆 / 对话日志，切勿上传）`r`n.workbuddy-ai/`r`n"
            if ($script:DryRun) {
                Write-Step '[演练] 会给 .gitignore 补上 .workbuddy-ai/。'
            }
            else {
                [IO.File]::WriteAllText($script:GitIgnore, $giAppended, (New-Object System.Text.UTF8Encoding $false))
                Write-WarnTip '.gitignore 刚才漏了 .workbuddy-ai/，现已自动补上并屏蔽。'
            }
        }
        $giCount = @(Get-Content -LiteralPath $script:GitIgnore -ErrorAction SilentlyContinue |
                     Where-Object { $_.Trim() -ne '' -and -not $_.Trim().StartsWith('#') }).Count
        Write-Info ('.gitignore 已存在，共 ' + $giCount + ' 条规则。')
    }
    else {
        Write-WarnTip '没有 .gitignore。'
        Write-Info '首次提交没有它，bin/ obj/ publish/ 这些构建产物会被一起提交进去，'
        Write-Info '而且进了历史就再也清不干净。'
        Write-Blank
        if (Read-YesNo '现在生成一份适用于本项目的 .gitignore？') {
            $giLines = @(
                '# 构建输出',
                'bin/',
                'obj/',
                'publish/',
                'TestResults/',
                '',
                '# NuGet（老式 packages 目录；用 PackageReference 时不会产生，留着以防万一）',
                'packages/',
                '',
                '# 编辑器与 IDE 的个人文件',
                '.vs/',
                '.idea/',
                '*.user',
                '*.suo',
                '*.userprefs',
                '',
                '# 调试符号与缓存（纯构建产物，非源码）',
                '*.pdb',
                '*.cache',
                '',
                '# 本目录自己产生的日志',
                '*.log',
                '',
                '# Windows 资源管理器产生的杂物',
                'Thumbs.db',
                'Desktop.ini',
                '',
                '# 发布演练产物',
                'AutoZip-*-win-x64.zip',
                'notes.md',
                'selftest-report.txt',
                '',
                '# 本机产生的临时说明（非源码，且含绝对路径，不应进版本库）',
                '执行打包命令.txt',
                '',
                '# WorkBuddy AI 的工作目录（含项目记忆 / 对话日志，切勿上传）',
                '.workbuddy-ai/'
            )
            if ($script:DryRun) {
                Write-Step ('[演练] 会写入 .gitignore，共 ' + $giLines.Count + ' 行。')
            }
            else {
                $enc = New-Object System.Text.UTF8Encoding $false
                [IO.File]::WriteAllText($script:GitIgnore, (($giLines -join "`r`n") + "`r`n"), $enc)
                Write-Good '.gitignore 已生成。'
            }
        }
        else {
            Write-WarnTip '跳过。稍后请自己确认没有把构建产物提交进去。'
        }
    }

    # -------------------------------------------------------------------
    #  1.5 本地关卡
    #
    #  首次提交要不要跑？要。第一次推上去的东西如果测试都过不了，CI 上那
    #  条红色记录会一直挂着。所以默认跑，嫌慢可以 -NoGate。
    # -------------------------------------------------------------------
    if (-not $script:NoGate) {
        Write-Head '第四步：本地关卡'
        Write-Info '第一次提交前先在本机跑一遍：单元测试 -> 单文件发布 -> 在发布产物上跑界面自检。'
        Write-Note '这一步能让 CI 上的第一次构建就是绿的。不想跑就用 -NoGate 跳过。'
        Write-Blank
        if (Read-YesNo '现在跑本地关卡？') {
            if ($script:DryRun) {
                Write-Step '[演练] 会执行 publish.cmd（含界面自检）。'
            }
            else {
                if (-not (Invoke-LocalGate)) {
                    Exit-Fail '本地关卡没过。没有提交任何东西，工作区和刚初始化时一样。'
                }
            }
        }
        else {
            Write-WarnTip '跳过本地关卡。CI 那边仍然会跑单元测试。'
        }
    }
    else {
        Write-Note '已指定 -NoGate，跳过本地关卡。'
    }

    # -------------------------------------------------------------------
    #  1.6 暂存并预览
    # -------------------------------------------------------------------
    Write-Head '第五步：暂存文件'

    if ($script:DryRun) {
        # 演练时仓库可能还没建起来（git init 也被跳过了），那 git ls-files
        # 根本没有意义。能用 git 就用 git —— 这样 .gitignore 才会被尊重；
        # 用不了就自己列举，至少让人看清这一把会甩多少个文件进去。
        $staged = @((Invoke-Git 'ls-files' '--others' '--exclude-standard').Lines)
        if ($staged.Count -eq 0) {
            $staged = @(Get-ChildItem -LiteralPath $script:Repo -Recurse -File -Force -ErrorAction SilentlyContinue |
                        Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
                        ForEach-Object { $_.FullName.Substring($script:Repo.Length).TrimStart('\', '/') })
        }
        Write-Step ('[演练] 会暂存约 ' + $staged.Count + ' 个文件。')
        if (-not $isRepo) {
            Write-Note '演练时 git init 也跳过了，.gitignore 还没生效，所以这个数字比实际偏大。'
        }
    }
    else {
        $r = Invoke-Git 'add' '-A'
        if ($r.Code -ne 0) { Exit-Fail 'git add -A 失败，没有任何东西被提交。' }

        $staged = @((Invoke-Git 'diff' '--cached' '--name-status').Lines)
        if ($staged.Count -eq 0) {
            # 未出生的分支上 diff --cached 偶尔取不到，退回按索引列文件
            $staged = @((Invoke-Git 'ls-files').Lines)
        }
        Write-Info ('已暂存 ' + $staged.Count + ' 个文件。')
    }

    # 揪出明显不该进版本库的产物。
    # 分隔符两种都要认：git 输出正斜杠，自己列举文件时拿到的是反斜杠。
    $suspicious = @($staged | Where-Object { $_ -match '(^|[/\\])(bin|obj|publish)[/\\]' })
    if ($suspicious.Count -gt 0) {
        Write-Blank
        Write-WarnTip ('里面有 ' + $suspicious.Count + ' 个文件在 bin/ obj/ publish/ 下面：')
        $suspicious | Select-Object -First 8 | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor Yellow }
        if ($suspicious.Count -gt 8) {
            Write-Host ('      ... 另有 ' + ($suspicious.Count - 8) + ' 个未列出') -ForegroundColor Yellow
        }
        Write-Blank
        if (-not (Read-YesNo '这些一般是构建产物。仍然继续？' 'n')) { Exit-Cancel }
    }

    # 死守：AI 工作目录（项目记忆 / 对话日志）绝不允许进版本库。
    # git add -A 会尊重 .gitignore，正常情况下这里不会命中；一旦命中说明
    # 忽略规则被改坏或文件被强制加入，直接拦下，绝不提交。
    $leakAI = @($staged | Where-Object { $_ -match '(^|[/\\])\.workbuddy-ai([/\\]|$)' })
    if ($leakAI.Count -gt 0) {
        Write-Blank
        Write-WarnTip ('.workbuddy-ai 工作目录混进了暂存区（共 ' + $leakAI.Count + ' 个文件）：')
        $leakAI | Select-Object -First 8 | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor Red }
        Write-Blank
        Exit-Fail '.workbuddy-ai 含项目记忆与对话日志，严禁上传。请确认 .gitignore 已屏蔽该目录后再发版。'
    }

    Write-Blank
    if ($staged.Count -le 25) {
        $staged | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor DarkGray }
    }
    else {
        $staged | Select-Object -First 20 | ForEach-Object { Write-Host ('      ' + $_) -ForegroundColor DarkGray }
        Write-Host ('      ... 另有 ' + ($staged.Count - 20) + ' 个未列出') -ForegroundColor DarkGray
    }

    # -------------------------------------------------------------------
    #  1.7 远端
    # -------------------------------------------------------------------
    Write-Head '第六步：远端仓库'

    $remoteUrl = Git-First 'remote' 'get-url' 'origin'

    if ($remoteUrl -eq '') {
        Write-WarnTip '还没有配置远端 origin，现在推不上去。'
        Write-Blank
        if (Read-YesNo '现在添加远端 origin？') {
            $remoteUrl = Read-WithDefault '远端地址' $script:DefaultRemote
            if ($script:DryRun) {
                Write-Step ('[演练] 会执行：git remote add origin ' + $remoteUrl)
            }
            else {
                $r = Invoke-Git 'remote' 'add' 'origin' $remoteUrl
                if ($r.Code -ne 0) { Exit-Fail ('添加远端 ' + $remoteUrl + ' 失败。') }
                Write-Good ('远端已添加：' + $remoteUrl)
            }
        }
        else {
            $remoteUrl = ''
            Write-WarnTip '跳过。这一次只会提交到本地，不推送。'
        }
    }
    else {
        Write-Info ('远端 origin：' + $remoteUrl)
    }

    # -------------------------------------------------------------------
    #  1.8 确认
    # -------------------------------------------------------------------
    Write-Head '第七步：确认'

    $firstVer = ''
    if (Test-Path -LiteralPath $script:PropsFile) {
        $m0 = [regex]::Match([IO.File]::ReadAllText($script:PropsFile), '<AssemblyVersion>(\d+\.\d+\.\d+)\.0</AssemblyVersion>')
        if ($m0.Success) { $firstVer = $m0.Groups[1].Value }
    }
    if ($firstVer -eq '') { $firstVer = '读不到' }

    Write-Info ('项目目录：' + $script:Repo)
    Write-Info ('当前分支：' + $branch)
    Write-Info ('程序版本：' + $firstVer)
    Write-Info ('提交身份：' + $cfgName + ' <' + $cfgEmail + '>')
    Write-Info ('暂存文件：' + $staged.Count + ' 个')
    if ($remoteUrl -ne '') { Write-Info ('远端 origin：' + $remoteUrl) }
    else                   { Write-Info '远端 origin：未配置（只提交到本地）' }
    if ($script:DryRun)    { Write-WarnTip '演练模式：不会真的提交。' }
    Write-Blank
    if (-not (Read-YesNo '按上面这些继续？')) { Exit-Cancel }

    # -------------------------------------------------------------------
    #  1.9 提交说明（手动输入）
    # -------------------------------------------------------------------
    $msg = Read-CommitMessage -Scene '首次提交' -Suggest ('初始提交：AutoZip ' + $firstVer)

    if ($script:DryRun) {
        Write-Head '演练结果'
        Write-Info '实际执行时会依次做这些：'
        Write-Blank
        Write-Host '      git add -A' -ForegroundColor DarkGray
        Write-Host ('      git commit -m "' + $msg.Subject + '"') -ForegroundColor DarkGray
        if ($msg.Body -ne '') {
            Write-Host ('      git commit -m "' + $msg.Body + '"') -ForegroundColor DarkGray
        }
        if ($remoteUrl -ne '') {
            Write-Host ('      git push -u origin ' + $branch) -ForegroundColor DarkGray
        }
        Write-Blank
        Write-WarnTip '演练结束。没有写入、提交或推送任何东西。'
        Exit-Script 0
    }

    # -------------------------------------------------------------------
    #  1.10 提交
    # -------------------------------------------------------------------
    Write-Head '第八步：提交'

    $commitArgs = @('commit', '-m', $msg.Subject)
    if ($msg.Body -ne '') { $commitArgs += @('-m', $msg.Body) }
    $r = Invoke-Git @commitArgs
    if ($r.Code -ne 0) {
        Exit-Fail '提交失败。前面的步骤都成功了，所以东西没丢，还留在暂存区里。'
    }

    $commitHash = Git-First 'rev-parse' '--short' 'HEAD'
    Write-Good ('已提交 ' + $commitHash + '：' + $msg.Subject)
    $hasHead = $true

    # -------------------------------------------------------------------
    #  1.11 打标签（首次提交可选）
    #
    #  不打也行 —— 打了才会触发 CI 建 Release。把这个因果说清楚，让人自己选。
    # -------------------------------------------------------------------
    $tagName = ''
    Write-Blank
    Write-Info '打标签才会触发 CI 打包并发 Release；不打就只是把代码推上去。'
    Write-Info '标签只写 X.Y.Z 就行（例如 6.6.6），v 前缀由脚本在后台自动补上。'
    if (Read-YesNo '给这次提交打一个版本标签？') {
        $tagName = Read-WithDefault '标签名' $firstVer
        # 用户只写 6.6.6 即可；即使手滑带了 v 也先剥掉，最后统一由脚本补回 v 前缀。
        $tagName = $tagName -replace '^v', ''
        if ($tagName -notmatch '^\d+\.\d+\.\d+$') {
            Write-WarnTip '标签名不是 X.Y.Z 的形式，CI 不会触发（工作流只认 v* 开头的标签）。'
            if (-not (Read-YesNo '仍然打这个标签？' 'n')) { $tagName = '' }
        }
        if ($tagName -ne '') { $tagName = 'v' + $tagName }
        if ($tagName -ne '') {
            if (Git-Ok 'rev-parse' '-q' '--verify' ('refs/tags/' + $tagName)) {
                Write-WarnTip ('标签 ' + $tagName + ' 已经存在，跳过。')
                $tagName = ''
            }
            else {
                $r = Invoke-Git 'tag' '-a' $tagName '-m' ('AutoZip ' + $firstVer)
                if ($r.Code -ne 0) {
                    Exit-Fail ('打标签 ' + $tagName + ' 失败。提交已经在了，可以手工补：git tag -a ' + $tagName + ' -m "AutoZip ' + $firstVer + '"')
                }
                Write-Good ('已打标签 ' + $tagName)
            }
        }
    }

    # -------------------------------------------------------------------
    #  1.12 推送
    # -------------------------------------------------------------------
    if ($remoteUrl -eq '') {
        Write-Head '首次提交完成'
        Write-Good ('本地已提交 ' + $commitHash + '，没有配置远端，所以没有推送。')
        Write-Blank
        Write-Info '以后要推送，先加上远端：'
        Write-Info ('    git remote add origin ' + $script:DefaultRemote)
        Write-Info ('    git push -u origin ' + $branch)
        Exit-Script 0
    }

    Write-Blank
    if (-not (Read-YesNo ('现在推送到 ' + $remoteUrl + ' ？'))) {
        Write-Blank
        Write-WarnTip '没有推送。东西都还在本地，想推的时候执行：'
        Write-Info ('    git push -u origin ' + $branch)
        if ($tagName -ne '') { Write-Info ('    git push origin ' + $tagName) }
        Exit-Script 0
    }

    Write-Blank
    Write-Info '第一次在这台机器上推送时，Git 凭据管理器会弹一个浏览器窗口让你登录 GitHub。'
    Write-Info '登录一次就会记在 Windows 凭据管理器里，以后不用再登。'

    # 远端还没有这个分支时要用 -u 把上游记下来
    $remoteHasBranch = ((Invoke-Git 'ls-remote' '--heads' 'origin' $branch).Lines.Count -gt 0)
    $pushArgs = if ($remoteHasBranch) { @('push', 'origin', $branch) } else { @('push', '-u', 'origin', $branch) }

    $r = Invoke-Git @pushArgs
    if ($r.Code -ne 0) {
        Write-Blank
        Write-Host '  [失败] 推送分支失败。' -ForegroundColor Red
        Write-Blank
        Write-Info '提交和标签都还在本地，GitHub 上什么都没有，所以也没有触发任何 Release。'
        Write-Info '常见原因：登录窗口被关掉了、登错了账号、或者没有网络。'
        Write-Blank
        Write-Info '处理完再执行这两条：'
        Write-Info ('    git push -u origin ' + $branch)
        if ($tagName -ne '') { Write-Info ('    git push origin ' + $tagName) }
        Exit-Script 1
    }
    Write-Good ('分支 ' + $branch + ' 已推送。')

    if ($tagName -ne '') {
        $r = Invoke-Git 'push' 'origin' $tagName
        if ($r.Code -ne 0) {
            Write-Blank
            Write-Host '  [失败] 分支推上去了，但标签没推上去，所以 CI 不会触发。' -ForegroundColor Red
            Write-Blank
            Write-Info '补推标签：'
            Write-Info ('    git push origin ' + $tagName)
            Exit-Script 1
        }
        Write-Good ('标签 ' + $tagName + ' 已推送。')
    }

    Write-Head '首次提交完成'
    if ($tagName -ne '') {
        Write-Info 'GitHub Actions 已经开始构建 Release 了。'
        Write-Blank
        Write-Info ('  看进度：' + $script:ActionsUrl)
        Write-Info ('  看结果：' + $script:ReleaseBase + $tagName)
        Write-Blank
        Write-Info '几分钟后会出现一个压缩包，里面是 AutoZip.exe 加 tools\7za.exe。'
    }
    else {
        Write-Info '代码已经推上去了。没有打标签，所以 CI 不会构建 Release。'
        Write-Info '以后要发版，执行 release.bat 并指定版本号即可。'
    }
    Exit-Script 0
}

# =====================================================================
#  常规发版
# =====================================================================

# ---------------------------------------------------------------------
#  当前分支
# ---------------------------------------------------------------------
Write-Head '第二步：分支'

$branch = Git-First 'symbolic-ref' '--short' '-q' 'HEAD'
if ($branch -eq '') {
    Exit-Fail 'HEAD 处在分离状态（不在任何分支上）。先切回一个分支再发版，例如：git checkout main'
}
Write-Info ('当前分支：' + $branch)

if ($branch -ne 'main') {
    Write-WarnTip ('HEAD 在 ' + $branch + ' 上，不是 main。')
    if (-not (Read-YesNo '仍然从这个分支发版？' 'n')) { Exit-Cancel }
}

# 与远端的差距
if (Git-Ok 'rev-parse' '-q' '--verify' ('refs/remotes/origin/' + $branch)) {
    $lr = (Invoke-Git 'rev-list' '--left-right' '--count' ('origin/' + $branch + '...HEAD')).Lines
    if ($lr.Count -gt 0 -and $lr[0] -match '^(\d+)\s+(\d+)$') {
        $behind = [int]$Matches[1]
        $ahead  = [int]$Matches[2]
        if ($behind -gt 0) {
            Write-WarnTip ('本地分支比远端落后 ' + $behind + ' 个提交。')
            Write-Info '    先拉取再发版：git pull --rebase'
            if (-not (Read-YesNo '仍然继续？' 'n')) { Exit-Cancel }
        }
        if ($ahead -gt 0) {
            Write-Note ('本地还有 ' + $ahead + ' 个提交没推到远端。')
        }
    }
}

# ---------------------------------------------------------------------
#  版本号
# ---------------------------------------------------------------------
Write-Head '第三步：版本号'

if (-not (Test-Path -LiteralPath $script:PropsFile)) {
    Exit-Fail ('找不到 ' + $script:PropsFile)
}

# 界面上那个版本号读的是 AssemblyVersion，不是 Version。所以「当前版本」
# 以 AssemblyVersion 为准，写的时候三处一起写。
$m = [regex]::Match([IO.File]::ReadAllText($script:PropsFile), '<AssemblyVersion>(\d+\.\d+\.\d+)\.0</AssemblyVersion>')
if (-not $m.Success) {
    Exit-Fail 'Directory.Build.props 里读不到 <AssemblyVersion>X.Y.Z.0</AssemblyVersion>。先把这个文件修好再发版。'
}
$curVer = $m.Groups[1].Value

Write-Info ('当前版本：' + $curVer)

if ($Version -eq '') {
    Write-Blank
    Write-Note '版本号只写在 Directory.Build.props 一处。标题栏和「关于」里的版本'
    Write-Note '都是运行时读程序集版本得来的，别处不抄第二份。'
    $Version = Read-WithDefault '新版本号（X.Y.Z，不带 v，直接回车表示不改版本）' $curVer
}

if ($Version -notmatch '^v?\d+\.\d+\.\d+$') {
    Exit-Fail ('「' + $Version + '」不是版本号。请用 X.Y.Z 的形式，例如 6.6.7。')
}
$Version = $Version -replace '^v', ''

$versionChanged = ($Version -ne $curVer)

if ($versionChanged) {
    Write-Info ('版本变化：' + $curVer + '  ->  ' + $Version)

    # 版本号比现在还小，多半是手滑
    $cmp = 0
    try {
        $cmp = ([version]($Version + '.0')).CompareTo([version]($curVer + '.0'))
    }
    catch { $cmp = 0 }

    if ($cmp -lt 0) {
        Write-WarnTip ('新版本号 ' + $Version + ' 比当前版本 ' + $curVer + ' 还小。')
        if (-not (Read-YesNo '确定要往回退？' 'n')) { Exit-Cancel }
    }
}
else {
    Write-Info '版本号没有变化，这一版按当前版本发。'
}

# ---------------------------------------------------------------------
#  标签冲突
# ---------------------------------------------------------------------
$tagName = 'v' + $Version

if (Git-Ok 'rev-parse' '-q' '--verify' ('refs/tags/' + $tagName)) {
    Exit-Fail ('标签 ' + $tagName + ' 在本地已经存在。换一个版本号，或者先删掉旧标签：git tag -d ' + $tagName)
}

$remoteUrl = Git-First 'remote' 'get-url' 'origin'
if ($remoteUrl -ne '') {
    $remoteTagHits = @((Invoke-Git 'ls-remote' '--tags' 'origin' $tagName).Lines |
                       Where-Object { $_ -match ('refs/tags/' + [regex]::Escape($tagName) + '$') })
    if ($remoteTagHits.Count -gt 0) {
        Exit-Fail ('标签 ' + $tagName + ' 在远端已经存在（本地没有，但远端有）。换一个版本号。')
    }
}
else {
    Write-WarnTip '还没有配置远端 origin。待会儿会问你要不要加。'
}

# ---------------------------------------------------------------------
#  工作区是否干净
#
#  这一步放在写版本号之前：此刻工作区里的改动全都是「别人的」，不是这次
#  的版本号改动。发版提交里最好只有版本号那一行，历史才说得清什么时候发
#  了什么。
# ---------------------------------------------------------------------
Write-Head '第四步：工作区'

$otherChanges = $false
if (-not (Git-Ok 'diff-index' '--quiet' 'HEAD' '--')) { $otherChanges = $true }
if ((Invoke-Git 'ls-files' '--others' '--exclude-standard').Lines.Count -gt 0) { $otherChanges = $true }

$Fold = $false
if ($otherChanges) {
    $statusLines = @((Invoke-Git 'status' '--short').Lines)
    Write-WarnTip '工作区不干净：'
    Write-Blank
    if ($statusLines.Count -le 25) {
        $statusLines | ForEach-Object { Write-Host ('      ' + $_) }
    }
    else {
        $statusLines | Select-Object -First 20 | ForEach-Object { Write-Host ('      ' + $_) }
        Write-Host ('      ... 另有 ' + ($statusLines.Count - 20) + ' 个未列出')
    }
    Write-Blank
    Write-Info '发版提交里最好只有版本号那一行，这样历史才说得清什么时候发了什么。'
    Write-Blank
    if (Read-YesNo '把这些改动一起塞进这次发版提交？' 'n') {
        $Fold = $true
    }
    else {
        Write-Note '好。那这次提交只带上版本号的改动。'
    }
}
else {
    Write-Info '工作区是干净的。'
}

# ---------------------------------------------------------------------
#  汇总确认
# ---------------------------------------------------------------------
Write-Head '第五步：确认'

Write-Info ('项目目录：' + $script:Repo)
Write-Info ('当前分支：' + $branch)
if ($versionChanged) { Write-Info ('版本变化：' + $curVer + '  ->  ' + $Version) }
else                 { Write-Info ('版本号：' + $Version + '（不改动）') }
Write-Info ('标签：' + $tagName)
if ($script:NoGate)         { Write-Info '本地关卡：跳过（已指定 -NoGate）' }
elseif ($script:NoSelfTest) { Write-Info '本地关卡：publish.cmd noselftest（跳过界面自检）' }
else                        { Write-Info '本地关卡：publish.cmd（单元测试 -> 单文件发布 -> 界面自检）' }
if ($Fold) { Write-Info '同时带上：上面列出的那些未提交改动' }
if ($remoteUrl -ne '') { Write-Info ('远端 origin：' + $remoteUrl) }
else                   { Write-Info ('远端 origin：尚未配置，待会儿会加 ' + $script:DefaultRemote) }
if ($script:DryRun)    { Write-WarnTip '演练模式：不会写入、提交、打标签或推送。' }
Write-Blank
if (-not (Read-YesNo '按上面这些开始？')) { Exit-Cancel }

# ---------------------------------------------------------------------
#  本地关卡
# ---------------------------------------------------------------------
if (-not $script:NoGate) {
    Write-Head '第六步：本地关卡'
    Write-Note '关卡放在写版本号之前：这样关卡一旦失败，工作区还是干干净净的原样。'
    Write-Blank
    if ($script:DryRun) {
        Write-Step '[演练] 会执行 publish.cmd。'
    }
    else {
        if (-not (Invoke-LocalGate)) {
            Exit-Fail '本地关卡没过。版本号没有改动，也没有提交或推送任何东西。'
        }
    }
}
else {
    Write-Blank
    Write-Note '已指定 -NoGate，跳过本地关卡。CI 那边仍然会跑单元测试。'
}

# ---------------------------------------------------------------------
#  写版本号
# ---------------------------------------------------------------------
Write-Head '第七步：写入版本号'

if (-not $versionChanged) {
    Write-Info '版本号没有变化，跳过写入。'
}
elseif ($script:DryRun) {
    Write-Step ('[演练] 会把 ' + $Version + ' 写进 Directory.Build.props 的三处。')
}
else {
    Write-Step ('正在把 ' + $Version + ' 写进 Directory.Build.props ...')

    $bytes  = [IO.File]::ReadAllBytes($script:PropsFile)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text   = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }

    $text = [regex]::Replace($text, '<Version>[^<]*</Version>', '<Version>' + $Version + '</Version>')
    $text = [regex]::Replace($text, '<AssemblyVersion>[^<]*</AssemblyVersion>', '<AssemblyVersion>' + $Version + '.0</AssemblyVersion>')
    $text = [regex]::Replace($text, '<FileVersion>[^<]*</FileVersion>', '<FileVersion>' + $Version + '.0</FileVersion>')

    [IO.File]::WriteAllText($script:PropsFile, $text, (New-Object System.Text.UTF8Encoding $hasBom))

    # 回读校验：三处都要对得上。静默写失败会发出一个「Release 页面写着
    # 6.6.7、程序标题栏写着 6.6.6」的包，那比当场报错难查得多。
    $back  = [IO.File]::ReadAllText($script:PropsFile)
    $esc   = [regex]::Escape($Version)
    $okVer = ($back -match ('<Version>' + $esc + '</Version>'))
    $okAsm = ($back -match ('<AssemblyVersion>' + $esc + '\.0</AssemblyVersion>'))
    $okFil = ($back -match ('<FileVersion>' + $esc + '\.0</FileVersion>'))

    if (-not ($okVer -and $okAsm -and $okFil)) {
        $miss = New-Object System.Collections.ArrayList
        if (-not $okVer) { [void]$miss.Add('Version') }
        if (-not $okAsm) { [void]$miss.Add('AssemblyVersion') }
        if (-not $okFil) { [void]$miss.Add('FileVersion') }
        Exit-Fail ('写进去了但读回来对不上：' + ($miss -join '、') + '。请手工检查 Directory.Build.props。')
    }

    Write-Good ('版本号已写入三处：Version / AssemblyVersion / FileVersion = ' + $Version)
}

# ---------------------------------------------------------------------
#  演练到此为止
# ---------------------------------------------------------------------
if ($script:DryRun) {
    Write-Head '演练结果'
    Write-Info '实际执行时会依次做这些：'
    Write-Blank
    if ($Fold) { Write-Host '      git add -A' -ForegroundColor DarkGray }
    else       { Write-Host '      git add -- Directory.Build.props' -ForegroundColor DarkGray }
    Write-Host '      提交说明：由你手动输入' -ForegroundColor DarkGray
    Write-Host ('      git tag -a ' + $tagName + ' -m "AutoZip ' + $Version + '"') -ForegroundColor DarkGray
    if ($remoteUrl -eq '') { Write-Host ('      git remote add origin ' + $script:DefaultRemote) -ForegroundColor DarkGray }
    Write-Host ('      git push origin ' + $branch) -ForegroundColor DarkGray
    Write-Host ('      git push origin ' + $tagName) -ForegroundColor DarkGray
    Write-Blank
    Write-Info ('然后 GitHub Actions 会构建并发布 AutoZip-' + $Version + '-win-x64.zip')
    Write-Blank
    Write-WarnTip '演练结束。没有写入、提交、打标签或推送任何东西。'
    Exit-Script 0
}

# ---------------------------------------------------------------------
#  暂存
# ---------------------------------------------------------------------
if ($Fold) { $r = Invoke-Git 'add' '-A' }
else       { $r = Invoke-Git 'add' '--' 'Directory.Build.props' }
if ($r.Code -ne 0) { Exit-Fail 'git add 失败。' }

$stagedCount = (Invoke-Git 'diff' '--cached' '--name-only').Lines.Count
Write-Info ('已暂存 ' + $stagedCount + ' 个文件。')

if ($stagedCount -eq 0) {
    Write-Blank
    Write-WarnTip '暂存区是空的 —— 版本号和上一次发版提交里的一样，没有新东西可以提交。'
    Write-Info ('也就是说，HEAD 上的内容就已经是 ' + $Version + ' 了。')
    Write-Blank
    if (-not (Read-YesNo ('直接给当前 HEAD 打标签 ' + $tagName + '，然后推送？') 'n')) { Exit-Cancel }
}

# ---------------------------------------------------------------------
#  提交（说明手动输入）
# ---------------------------------------------------------------------
Write-Head '第八步：提交'

if ($stagedCount -gt 0) {
    $suggest = if ($versionChanged) { ('release: ' + $Version) } else { ('release: ' + $Version + '（版本号未变）') }
    $msg = Read-CommitMessage -Scene '常规发版' -Suggest $suggest

    $commitArgs = @('commit', '-m', $msg.Subject)
    if ($msg.Body -ne '') { $commitArgs += @('-m', $msg.Body) }
    $r = Invoke-Git @commitArgs
    if ($r.Code -ne 0) {
        Exit-Fail '提交失败。版本号已经写进 Directory.Build.props 了，改动都还在暂存区里，没有丢。'
    }

    $commitHash = Git-First 'rev-parse' '--short' 'HEAD'
    Write-Good ('已提交 ' + $commitHash + '：' + $msg.Subject)
}
else {
    $commitHash = Git-First 'rev-parse' '--short' 'HEAD'
    Write-Info ('没有新提交，直接给现有的 ' + $commitHash + ' 打标签。')
}

# ---------------------------------------------------------------------
#  打标签
# ---------------------------------------------------------------------
Write-Blank
Write-Step ('正在打标签 ' + $tagName + ' ...')
$r = Invoke-Git 'tag' '-a' $tagName '-m' ('AutoZip ' + $Version)
if ($r.Code -ne 0) { Exit-Fail ('打标签 ' + $tagName + ' 失败。提交已经在了，标签可以手工补。') }
Write-Good ('已打标签 ' + $tagName)

# ---------------------------------------------------------------------
#  远端
# ---------------------------------------------------------------------
if ($remoteUrl -eq '') {
    Write-Blank
    if (Read-YesNo ('还没有远端。现在添加 ' + $script:DefaultRemote + ' 并推送？')) {
        $r = Invoke-Git 'remote' 'add' 'origin' $script:DefaultRemote
        if ($r.Code -ne 0) { Exit-Fail '添加远端失败。提交和标签都在本地，没有丢。' }
        $remoteUrl = $script:DefaultRemote
        Write-Good ('远端已添加：' + $remoteUrl)
    }
    else {
        Write-Blank
        Write-WarnTip '没有推送。提交和标签都还在本地，想推的时候执行：'
        Write-Info ('    git push -u origin ' + $branch)
        Write-Info ('    git push origin ' + $tagName)
        Exit-Script 0
    }
}

# ---------------------------------------------------------------------
#  推送
# ---------------------------------------------------------------------
Write-Head '第九步：推送'

Write-Info '第一次在这台机器上推送时，Git 凭据管理器会弹一个浏览器窗口让你登录 GitHub。'
Write-Info '登录一次就会记在 Windows 凭据管理器里，以后不用再登。'
Write-Blank

$remoteHasBranch = ((Invoke-Git 'ls-remote' '--heads' 'origin' $branch).Lines.Count -gt 0)
$pushArgs = if ($remoteHasBranch) { @('push', 'origin', $branch) } else { @('push', '-u', 'origin', $branch) }

$r = Invoke-Git @pushArgs
if ($r.Code -ne 0) {
    Write-Blank
    Write-Host '  [失败] 推送分支失败。' -ForegroundColor Red
    Write-Blank
    Write-Info '提交和标签都在本地，GitHub 上没有，所以没有触发任何 Release。'
    Write-Info '常见原因：登录窗口被关掉了、登错了账号、或者没有网络。'
    Write-Blank
    Write-Info '处理完再执行这两条：'
    Write-Info ('    git push -u origin ' + $branch)
    Write-Info ('    git push origin ' + $tagName)
    Write-Blank
    Write-Info '想把这次的提交和标签收回来，执行：'
    Write-Info ('    git tag -d ' + $tagName)
    Write-Info '    git reset --soft HEAD~1'
    Exit-Script 1
}
Write-Good ('分支 ' + $branch + ' 已推送。')

# 这个推送才是触发发版的那个。工作流认的是标签，不是分支。
$r = Invoke-Git 'push' 'origin' $tagName
if ($r.Code -ne 0) {
    Write-Blank
    Write-Host '  [失败] 分支推上去了，但标签没推上去，所以 CI 不会触发。' -ForegroundColor Red
    Write-Blank
    Write-Info '补推标签：'
    Write-Info ('    git push origin ' + $tagName)
    Exit-Script 1
}
Write-Good ('标签 ' + $tagName + ' 已推送。')

# ---------------------------------------------------------------------
#  收尾
# ---------------------------------------------------------------------
Write-Head '发版已提交给 CI'

Write-Info 'GitHub Actions 正在从这个标签重新编译并打包。'
Write-Blank
Write-Info ('  看进度：' + $script:ActionsUrl)
Write-Info ('  看结果：' + $script:ReleaseBase + $tagName)
Write-Blank
Write-Info ('几分钟后会出现一个压缩包：AutoZip-' + $Version + '-win-x64.zip')
Write-Blank
Write-Info '如果那次构建失败了，标签已经在 GitHub 上了。删掉重来：'
Write-Info ('    git push origin :refs/tags/' + $tagName)
Write-Info ('    git tag -d ' + $tagName)
Write-Blank

Exit-Script 0
