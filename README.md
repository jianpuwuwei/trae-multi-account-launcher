# Trae Multi-Account Launcher

One-click launch multiple isolated Trae CN instances on Windows — each instance can sign in with a different account. No Sandboxie, no external sandbox, no kernel driver.

中文名：**Trae 多账号隔离启动器 / 一键双开 Trae CN 客户端**

---

## 项目介绍

Trae CN 客户端默认只使用一份用户配置（`%APPDATA%\Trae CN`），同一台电脑上无法同时登录两个账号。本项目在用户态实现了一套轻量级「隔离容器」：

- 为每个账号准备独立的 profile 目录；
- 启动时通过环境变量把 `user-data`、扩展、缓存、临时目录全部重定向到容器内；
- 由此每个实例拥有独立的登录态，可同时在线不同账号。

全部逻辑自行实现，不依赖 Sandboxie 等第三方沙箱工具；同时附带命令行管理（启动、停止、重置、状态查询），停止时会清理整棵进程树。

## 功能特性

- **一键双开**：双击 `一键双开.cmd` 即可拉起「主实例 + 2 号隔离实例」
- **完全自研**：环境变量重定向 + 命令行标记 + 进程树管理，无外部依赖
- **真隔离登录态**：每个容器独立 `user-data`、扩展、Cache、`AppData`、`TEMP`、`USERPROFILE`
- **支持多实例**：不止双开，`start n` 可开 2~99 号任意容器
- **可控退出**：`stop` 终止主进程树并按容器标记清扫残留进程
- **一键换号**：`reset n` 停止容器并删除容器目录，等价于退出登录 + 清缓存
- **自动探测客户端路径**：运行进程 → 卸载注册表 → 常见安装目录

## 工作原理（用户态隔离）

| 环节 | 做法 |
|---|---|
| 配置隔离 | 启动子进程时写入 `VSCODE_PORTABLE`、`APPDATA`、`LOCALAPPDATA`、`TEMP/TMP`、`USERPROFILE`，全部指向 `<容器目录>` |
| 耦合变量清理 | 删除从父进程继承的 `VSCODE_IPC_HOOK*`、`VSCODE_EXTENSIONS`、`ELECTRON_RUN_AS_NODE` 等 |
| 进程识别 | 命令行显式带 `--user-data-dir=<容器路径>`，作为该容器的唯一标记 |
| 状态查询 | 通过 WMI 查询 `Win32_Process` 命令行的容器标记，列出全部 PID |
| 停止 | `taskkill /PID <主进程> /T /F` 带走进程树，再按标记清扫孤儿进程，并用 Restart Manager 精确清理锁定容器文件的浏览器进程 |
| 重置 | 先停容器，再递归删除目录；自动清除只读文件属性，遇到占用则定位占用进程后重试 |

> 说明：曾尝试用命名 Job Object 托管进程树，但 Windows 在最后一个 Job 句柄关闭时会销毁作业对象，启动器退出后无法再按名字访问，因此改为上述标记识别方案。

**边界**：这是配置/缓存/登录态级别的用户态隔离，不做内核级文件系统与注册表拦截；若需要强制沙箱，需要签名驱动，不在本项目范围内。

## 环境要求

- Windows 10 / 11（x64）
- .NET Framework 4.x（Windows 自带，无需安装 SDK）
- 已安装 Trae CN 客户端

## 快速开始

1. 下载或克隆本仓库；
2. 双击 `一键双开.cmd`；
3. 若主实例未运行，它会先启动主实例，再打开 2 号隔离实例；
4. 在第二个窗口登录另一个账号即可。

容器首次启动会自动创建在：

```
%LOCALAPPDATA%\TraeSandbox\account-2\
├─ portable\user-data      # 登录态、设置、缓存
├─ AppData\Roaming
├─ AppData\Local
├─ Temp
├─ Home                    # 容器内置用户目录（扩展、签到状态在这里）
├─ workspace               # 容器启动时自动打开的工作区（扩展加载必需）
└─ container.ini           # 记录主进程 PID、路径、启动时间
```

## 命令行用法

```
TraeSandbox.exe dual [n]    启动主实例（若未运行）+ n 号隔离容器（默认 2）
TraeSandbox.exe start n     只启动 n 号隔离容器
TraeSandbox.exe stop n      停止 n 号隔离容器
TraeSandbox.exe stop all    停止全部隔离容器
TraeSandbox.exe reset n     清空 n 号容器（退出登录并清除缓存）
TraeSandbox.exe status      查看容器状态
```

示例：

```powershell
# 开第三个账号
.\bin\TraeSandbox.exe start 3

# 查看所有容器
.\bin\TraeSandbox.exe status

# 账号 2 换号（清空登录态）
.\bin\TraeSandbox.exe reset 2

# 全部关闭
.\bin\TraeSandbox.exe stop all
```

`status` 输出示例：

```
容器根目录：C:\Users\<user>\AppData\Local\TraeSandbox
account-2  运行中  PID: 44992, 39332, 34052, ...
account-3  已停止
主实例：运行中
```

## 配置 `config.ini`

```ini
# Trae CN 主程序路径；留空则自动探测
traePath=

# 容器根目录
rootDir=%LOCALAPPDATA%\TraeSandbox

# 是否隔离 USERPROFILE（1=是，0=否）
isolateHome=1

# 启动容器前自动复制到容器扩展目录的扩展文件夹（多个用 ; 分隔）
extensionsDir=%USERPROFILE%\.trae-cn\extensions\local.trae-auto-checkin-1.0.0
```

- 自动探测顺序：正在运行的 Trae 进程 → 卸载注册表（HKLM/HKCU，含 WOW6432Node）→ 常见安装目录。
- 自动探测失败时，请手动填写 `traePath`，例如 `traePath=C:\Program Files\Trae CN\Trae CN.exe`。

## 在沙箱实例里安装扩展（如自动签到插件）

容器实例的扩展目录由 `USERPROFILE` 决定，隔离模式下位于：

```
%LOCALAPPDATA%\TraeSandbox\account-N\Home\.trae-cn\extensions\
```

Trae 不会只因为「目录里多了一个扩展文件夹」就加载它，必须满足两点，启动器已自动处理：

1. **写入扩展索引** `<容器扩展目录>\extensions.json`，并从 `.obsolete` 中清除该扩展（否则会被当作已移除的残留扩展）；
2. **容器窗口必须打开一个工作区**，否则 Trae 进入 Solo Lite 形态，用户扩展（含本插件）不会加载。启动器为此会自动打开 `<容器>\workspace`，无需手工干预。

### 方式一：自动同步（推荐）

1. 先把扩展安装到宿主客户端，确认目录存在，例如：
   `%USERPROFILE%\.trae-cn\extensions\local.trae-auto-checkin-1.0.0`
2. 在 `config.ini` 中填写要同步的扩展文件夹（多个用 `;` 分隔）：

   ```ini
   extensionsDir=%USERPROFILE%\.trae-cn\extensions\local.trae-auto-checkin-1.0.0
   ```

3. 重新启动容器（`start n` 或 `dual`），出现 `已同步扩展：local.trae-auto-checkin-1.0.0` 即成功。

同步时若容器已存在同名扩展目录会跳过复制，但每次启动都会确保索引存在。

### 方式二：手动复制

容器关闭后，把宿主的扩展文件夹整个复制过去（随后任选一次 `start n`，启动器会补写索引）：

```powershell
$name = 'local.trae-auto-checkin-1.0.0'
$src  = "$env:USERPROFILE\.trae-cn\extensions\$name"
$dst  = "$env:LOCALAPPDATA\TraeSandbox\account-2\Home\.trae-cn\extensions"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Recurse -Force $src $dst
```

然后重新 `start 2` 即可。

### 如何确认扩展已生效

容器启动并**完成登录**后（未登录时只显示登录窗口，扩展不会激活），检查：

- `<容器>\Home\.trae-cn\extensions\extensions.json` 中存在该扩展的条目；
- `<容器>\Home\.trae-cn\auto-checkin\state.json` 已生成（自动签到插件的激活标志）；
- 客户端「输出」面板中可看到「Trae 自动签到」频道日志。

> 注意：扩展目录隔离依赖 `isolateHome=1`。若设为 0，容器会直接使用宿主 `%USERPROFILE%\.trae-cn\extensions`，此时宿主已安装的扩展天然可用，但不再隔离。


## 目录结构

```
trae-sandbox/
├─ 一键双开.cmd          # 双击入口
├─ build.ps1             # 用系统自带 csc 重新编译
├─ config.ini            # 配置
├─ bin/TraeSandbox.exe   # 已编译好的 x64 程序
└─ src/
   ├─ Program.cs         # CLI 与客户端路径探测
   ├─ SandboxContainer.cs# 容器：环境重定向 / 启动 / 停止 / 重置
   └─ Win32Api.cs        # 带自定义环境块创建进程、窗口前置
```

## 自行编译

无需 Visual Studio 或 .NET SDK，使用系统自带的 .NET Framework 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

输出：`bin\TraeSandbox.exe`

## 常见问题

**主实例已经开着，`dual` 会做什么？**
只拉起隔离容器，不会重复启动主实例。

**容器实例显示已停止，但进程还在？**
先执行 `status` 确认；若仍有残留，`stop n` 会按容器标记清扫；必要时用管理员权限再执行一次。

**想彻底重来 / 换账号？**
`reset n` 删除容器目录，下次启动就是全新的登录态。

**会不会影响主实例的登录？**
不会。容器使用独立 `user-data`，宿主 `%APPDATA%\Trae CN` 不会被复用或修改。

## 免责声明

- 本项目仅用于在同一台自有设备上管理自己的多个账号，请遵守 Trae 的服务条款与当地法律法规。
- 本项目不包含、不修改 Trae 客户端，不读取或解密任何本地凭据；登录鉴权完全由客户端自身完成。
- 使用风险由使用者自行承担。
