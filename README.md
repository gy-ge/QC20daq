# QC20daq

QC20daq is a Windows data acquisition utility for the Renishaw QC20-W ballbar. It displays live displacement readings in a local web page and records samples to CSV. The web service and the 32-bit acquisition process run separately.

[Download the Windows x64 portable package](https://github.com/gy-ge/QC20daq/releases/latest)

## Quick start

### Requirements

- An x64 Windows PC with Bluetooth and .NET Framework 4.8 or a compatible later 4.x version.
- Renishaw Ballbar Trace installed. By default, QC20daq loads its device libraries from `C:\Program Files (x86)\Renishaw Ballbar Trace`. The main executable includes its own .NET runtime.
- A QC20-W with power and a recent web browser.

### Connect and record

Use the language menu in the page header to switch between **中文** and **English**. On the first visit, the page uses Chinese for Chinese browser settings and English otherwise. The browser remembers your choice. The names in parentheses below are the Chinese labels.

1. **Launch.** Extract the complete ZIP to a writable local folder. Keep `collector/` next to `QC20daq.exe`, then double-click the EXE. The application opens [http://127.0.0.1:8765/](http://127.0.0.1:8765/) in your default browser.
2. **Connect.** Wake the ballbar and place it near the Bluetooth receiver. Select **Device** (实机). Select the device saved by Trace (Trace 历史设备), or click **Search devices** (搜索设备) and select the ballbar by serial number. Choose a nominal sampling rate (up to 1000 Hz), then click **Connect** (连接). Check the live readings, curve, and range indicator before recording. To try the interface without a ballbar, select **Simulation** (模拟).
3. **Record.** Choose **Manual** (手动) or **Timed** (按时长). Enter a duration in seconds for a timed run, then click **Start recording** (开始记录).
4. **Save.** Click **Stop and save** (停止并保存) to end a recording manually. Timed recordings save automatically when the requested duration is reached. Find the CSV and capture report under **Saved records** (保存记录). The live preview continues after saving.
5. **Shut down.** Click **Shut down service** (关闭服务) in the upper-right corner. If a recording is active, confirm to save the data and release the device before the service exits. Closing the browser tab leaves the service running. Double-click the EXE to reopen it.

### Data files

QC20daq creates a `data/` directory beside the EXE. Hardware recordings go to `data/device/` and simulations to `data/simulated/`. The session folder and CSV filename use the local recording start time, for example `data/device/web_YYYYMMDD_HHMMSS/ballbar_YYYYMMDD_HHMMSS.csv`.

The CSV contains the sample index, nominal sample time in seconds, displacement in micrometres, and elapsed host receive time in seconds. Nominal time is derived from the sample index and configured rate. Host receive time includes Bluetooth delivery delays. The accompanying `capture.json` reports the stop reason, sampling rate, received and written sample counts, host queue drops, and range-limit counts.

### Troubleshooting

- **Device not found.** Check Bluetooth and ballbar power. If Ballbar Trace is using the device, release its connection before searching again.
- **Readings stop updating.** Wait for fresh samples before starting a recording. If the connection ends during a recording, QC20daq saves the samples already received and marks the session as interrupted.
- **The page does not respond.** Double-click the EXE to reopen the local page and check that the browser is up to date. The default log directory is `%LOCALAPPDATA%\Codex\Runtimes\dynamic-error\rotary\logs`.

## Build and verify

Open `QC20daq.slnx` to work on the complete solution.

| Project | Purpose |
| --- | --- |
| `QC20daq` | Local C# web service and web UI |
| `QC20daq.Collector` | Independent x86 acquisition process |
| `QC20daq.Verification` | End-to-end checks against a published executable |

Build on Windows with the .NET 10 SDK, .NET Framework 4.8 reference assemblies, and the Ballbar Trace device libraries. Set `-p:TraceDirectory=<directory>` at build time if Trace is installed in a different location. Build intermediates go to `%LOCALAPPDATA%\Codex\Runtimes\QC20daq\build` by default.

~~~powershell
$qc20Publish = "$env:LOCALAPPDATA\Codex\Runtimes\QC20daq\release"
dotnet build .\QC20daq.slnx -c Release
dotnet publish .\QC20daq\QC20daq.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $qc20Publish
~~~

The portable archive contains `QC20daq.exe`, `collector/Qc20Capture.exe`, `collector/Qc20Capture.exe.config`, and `Quickstart.txt`. Ballbar Trace supplies the device libraries on the target PC. At run time, `--install <directory>` selects another Trace installation. Use `--port <port>` and `--runtime <directory>` for an isolated instance. `DYNAMIC_ERROR_RUNTIME` can also set the runtime directory.

Use an unused port and a new test directory to verify the release alongside an active instance.

~~~powershell
$qc20TestRun = "$env:LOCALAPPDATA\Codex\Runtimes\QC20daq\verification\new-run"
dotnet run --project .\QC20daq.Verification\QC20daq.Verification.csproj -c Release -- "$qc20Publish\QC20daq.exe" $qc20TestRun 8766
~~~

The verifier copies the published files into the test directory. It checks 1000 Hz preview and recording, reattachment after a web service restart, continued preview after saving, reconnection, and saving on shutdown. It also validates the CSV row by row and checks the SHA-256 of the download. Results are written to `verification.json`. Add `--device` to test a connected ballbar, or add `--device --seconds 1800` for a 30-minute recording followed by a 30-second preview check.
