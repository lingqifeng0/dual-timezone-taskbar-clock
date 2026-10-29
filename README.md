# 双时区任务栏时钟 · Dual Time Zone Taskbar Clock

在 Windows 11 原生任务栏时钟位置自定义两行内容，显示 Windows 系统时区与中国标准时间（UTC+8）。程序独立运行，不需要 Windhawk；不会更改系统时区或系统文件。

> **兼容性限制：本版本只针对 Windows 11 x64 构建 26200.9711 的特定任务栏组件验证。** 程序会校验 `SystemTray.dll` 的 SHA-256、模块头和调用位置；组件不匹配时拒绝启用。Windows 更新或不同电脑可能需要重新适配，不能只靠更改版本号或跳过校验来保证安全。

## 功能

- 两行可分别显示系统时区和中国时间，也可以显示对应时区的日期。
- 设置面板支持调整时区顺序、标签、时间格式、日期格式以及每行模板。
- 可为当前用户启用登录 Windows 后自动启动；等待尚未加载的任务栏并重试。
- 程序在后台常驻，设置面板关闭后不必保持打开。

此版本以本机系统时区作为第一时区，中国标准时间固定使用 UTC+8。当前没有任意第三时区选项，也不支持实时秒数。

## 从源码构建

需要 Windows x64、.NET Framework 4.x 的 64 位 C# 编译器，以及 [Zig](https://ziglang.org/download/)（已用 Zig 0.15.2 构建）。运行独立测试还需要 Python 3。

在项目根目录打开 PowerShell：

```powershell
.\build.ps1
```

如果 PowerShell 阻止本地脚本运行，可以仅针对这次构建执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`。

如果 `zig.exe` 不在 PATH，可以指定它的路径：

```powershell
.\build.ps1 -ZigPath 'C:\tools\zig\zig.exe'
```

生成文件在 `dist`：`TimeZoneClock.exe`、`ClockNativeV2.dll` 和初始 `config.json`。程序与 DLL 需要放在同一个可写文件夹；重新构建不会覆盖该目录已有的配置。

要运行独立测试：

```powershell
.\build.ps1 -Test
```

也可以同时传入 `-ZigPath` 和 `-PythonPath`；测试默认使用 Windows Python 启动器 `py -3`，如果没有安装启动器，请用 `-PythonPath` 指定 Python 3 可执行文件。测试包含格式预览、启动选项、自动重试以及在独立测试进程中安装和恢复 DLL 钩子；测试不会对 Explorer 或真实的登录启动项操作。真实登录流程仍需在目标机器自行验证。

## 使用

1. 双击 `dist\TimeZoneClock.exe`。第一次运行会打开设置面板；之后可以双击通知区域的图标或再次运行程序打开设置。
2. 设置两行模板并选择时区顺序，点击“保存并应用”。Windows 任务栏可能到下一分钟才显示更新后的格式。
3. 如需自启动，勾选“登录 Windows 时自动启动”，然后保存。要取消自启动，取消勾选并保存。移动程序前应先关闭自启动，移动后重新启用，以便更新路径。
4. 退出时，右键通知区域的时钟图标，选择“退出并恢复原时钟”。关闭设置面板不会退出后台程序。

模板变量：`%label%` 为该行的标签，`%time%` 为该时区时间，`%date%` 为该时区日期，`%%` 显示百分号。例如 `%label% %time% %date%`。内容过长可能被任务栏截断；时间和日期格式使用 Windows 的格式规则。

如果更换了 DLL 后提示已有不同版本的时钟模块，先退出本程序，再重启 Windows 资源管理器或重新登录，让旧 DLL 从进程中卸载。

## 工作原理与限制

控制程序将项目自己的 DLL 加载进 `explorer.exe`，在经过版本和调用位置检查后替换 `SystemTray.dll` 的两个时间格式化导入项。退出或控制进程结束时会尝试恢复调用；为避免在线程仍可能使用 DLL 时强制卸载，DLL 可以保持加载但不再修改时钟，直到资源管理器退出。由于依赖未公开的任务栏内部细节，系统更新可能使它停止工作，也可能与其他时钟修改工具冲突。

启用登录自启动时，程序为当前用户写入 Windows `Run` 注册表项；配置文件保存在程序目录。它不需要网络服务。

## 致谢与许可证

Windows 任务栏时钟相关研究参考了 [m417z 的 Windhawk Taskbar Clock Customization](https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-clock-customization.wh.cpp)，该项目以 GPLv3 发布；本项目运行时不依赖 Windhawk。

本项目以 [GNU General Public License v3.0](LICENSE) 发布。
