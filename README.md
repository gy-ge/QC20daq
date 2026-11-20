# QC20 采集台

QC20 采集台通过蓝牙连接雷尼绍 QC20-W 球杆仪，在本机网页预览位移并保存 CSV。Web 服务和 32 位采集进程分别运行。

## 换电脑使用

目标电脑需使用 64 位 Windows，启用蓝牙，安装雷尼绍 Ballbar Trace，并具备 .NET Framework 4.8 或兼容的后续 4.x 运行环境。主程序自带 .NET 运行时。双击启动时，默认从 `C:\Program Files (x86)\Renishaw Ballbar Trace` 加载设备库。

1. **启动。** 将整个 ZIP 解压到有写权限的本地目录，例如 `D:\QC20daq`。将 `collector` 文件夹与 `QC20daq.exe` 放在同一级。双击 `QC20daq.exe`，默认浏览器打开采集页。也可手动打开 [本机采集页](http://127.0.0.1:8765/)。
2. **连接。** 唤醒球杆并靠近蓝牙接收器。网页选择「实机」。有 Trace 历史设备时直接选用，或点击「搜索设备」后按序列号选择。选采样率并点「连接」。出现「已连接」后，核对位移、量程和实时曲线。
3. **记录。** 选「手动」或「按时长」，填写定时模式的目标秒数，再点击「开始记录」。网页显示「记录中」及已写入的点数。
4. **保存。** 手动模式点「停止并保存」，定时模式到达设定时长后自动保存。确认「保存记录」出现新会话。可从该行下载 CSV 和报告，保存后仍可实时预览。
5. **关闭服务。** 点击网页右上角的「关闭服务」。若正在记录，确认后会先写完数据，再释放球杆并退出。看到「本机服务已关闭」即可关标签页。直接关标签页时，后台继续运行。下次双击 EXE，再点「连接」即可使用。

## 数据位置

启动时会在 EXE 同级自动创建 `data`。实机记录在 `data/device/`，模拟记录在 `data/simulated/`。会话目录和 CSV 按开始记录时的本机秒级时间命名，例如 `data/device/web_20261120_082422/ballbar_20261120_082422.csv`。CSV 包含点序、标称采样时间（s）、位移（µm）和主机相对接收时间（s）。配套 `capture.json` 记录结束原因、采样率、接收数、写入数、主机队列丢点和量程触限。

## 常见情况

- **找不到球杆。** 确认球杆已唤醒、电脑蓝牙已打开，并释放 Ballbar Trace 中已占用的球杆连接。回采集页重新搜索。
- **读数停更。** 等待样本恢复后再开录。持续断流时页面保留末次曲线并显示原因，正在记录的已接收样本随报告保存。
- **网页未响应。** 核对地址，使用已更新的浏览器，再双击 EXE 打开采集页。日志在本机运行目录的 `rotary/logs/`。

## 工程与发布

- `QC20daq.slnx` 打开完整工程。
- `QC20daq` 提供 C# 本机 Web 服务，网页文件位于 `Web/`。
- `QC20daq.Collector` 构建独立的 x86 设备进程。
- `QC20daq.Verification` 对发布后的 EXE 进行隔离验证。

开发时需 .NET 10 SDK、.NET Framework 4.8 引用程序集和本机 Trace 设备库。设定 `-p:TraceDirectory=<目录>` 可指定其他 Trace 安装位置。构建中间文件默认在 `%LOCALAPPDATA%\Codex\Runtimes\QC20daq\build`。

~~~powershell
dotnet build .\QC20daq.slnx -c Release
dotnet publish .\QC20daq\QC20daq.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$env:LOCALAPPDATA\Codex\Runtimes\QC20daq\release"
~~~

发布包包含主程序、`collector/Qc20Capture.exe`、`collector/Qc20Capture.exe.config` 和使用说明。设备库由目标电脑上的 Ballbar Trace 提供。启动参数 `--install <目录>` 指定设备库位置，`--port <端口>` 和 `--runtime <目录>` 用于隔离实例。默认运行目录位于 `%LOCALAPPDATA%\Codex\Runtimes\dynamic-error\`，可用 `DYNAMIC_ERROR_RUNTIME` 设置。

与现用服务并行验证时，选择独立端口和全新的外置运行目录。下面的检查覆盖 1000 Hz 预览、记录、Web 重启恢复、保存后预览、断开重连和记录中关闭服务，并逐行校验 CSV。需要接入实机时追加 `--device`。

~~~powershell
dotnet run --project .\QC20daq.Verification\QC20daq.Verification.csproj -c Release -- "$env:LOCALAPPDATA\Codex\Runtimes\QC20daq\release\QC20daq.exe" "$env:LOCALAPPDATA\Codex\Runtimes\QC20daq\verification\new-run" 8766
~~~

一次新测试使用一个空运行目录。核验程序将发布包复制到该目录的 `app/`，测试数据在 `app/data/`，日志在 `rotary/logs/`。验收结果写入 `verification.json`。

追加 `--device --seconds 1800` 可在实机上做 30 分钟定时采集验收。该模式在保存后继续观察 30 秒，逐行核对 CSV 并比对网页下载的 SHA-256。球杆静放可用于核对采集链路，报告会同时列出量程触限点数。
