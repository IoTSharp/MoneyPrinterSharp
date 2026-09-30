# MPS 本地媒体性能基线

独立的 .NET 10 控制台测量工具；无 NuGet 包、模型 API 或付费调用。FFmpeg 自行生成 `testsrc2` 色彩图案与 440 Hz 音调，不读取任何用户媒体，不属于真实软件功能证据。横屏 1920×1080 与竖屏 1080×1920 均为 30fps、15 秒，包含 H.264 视频和 AAC 音频。

## 运行

在仓库根目录使用 PowerShell 7、.NET 10 与 PATH 中的 FFmpeg/FFprobe：

```powershell
dotnet build prototypes/MediaBaseline/MediaBaseline.csproj --disable-build-servers -p:UseSharedCompilation=false -m:1
dotnet run --project prototypes/MediaBaseline/MediaBaseline.csproj --no-build -- --help
# 先在已存在的目录中选择一个新文件名，运行极小预检。
dotnet run --project prototypes/MediaBaseline/MediaBaseline.csproj --no-build -- --preflight-only --output .runs/media-preflight.json
# 正式测量也会先运行预检；已有报告不会被覆盖。
dotnet run --project prototypes/MediaBaseline/MediaBaseline.csproj --no-build -- --output .runs/media-baseline.json
```

`--ffmpeg PATH` 和 `--ffprobe PATH` 可指定已配置的可执行文件。运行前关闭本任务的并发构建与其他基准；保留实际后台负载的说明。报告不会收集用户名、机器名、硬件序列号或私有路径；GPU 名称与驱动需要在配套验收记录中人工记录，此工具只使用 CPU 编解码。报告是单次观测，不承诺跨机器一致。

## 测量口径

先用 160×90、10fps 的两帧片段验证生成与完整解码。正式测量对每种画幅依次执行：一次样本生成、三次 FFprobe 探测、2/7/12 秒三个固定点各一次定位并解码一帧、一次完整 15 秒转码、输出参数探测和一次完整音视频解码。实际任务数 24，全部串行。三次探测是同一文件的连续读取，操作系统缓存未清空，不能称为冷缓存数据。不同定位点不是同一位置的重复测量。

视频参数为 CPU libx264、veryfast、CRF 23、GOP 60、yuv420p；音频为 48kHz、AAC 128kbps。编解码各限制两线程，过滤器限制一线程，但 FFmpeg 进程还可能创建其他服务线程。每阶段耗时包含进程启动、标准输出读取和退出等待，不含显示、UI 调度、波形/缩略图生成、多轨混合或保存项目。峰值工作集来自 Windows `K32GetProcessMemoryInfo`，每 100ms 读取一次并在退出后补读，记录子进程自身峰值，不是系统峰值或整个产品内存。读取不到时为 0，不能解释为无内存开销。

成功必须满足 H.264/AAC、正确尺寸、30fps、约 15 秒及 450 个实际解码视频帧；定位任务必须解码恰好一帧。完整解码启用 `-xerror`，只启动进程或探测返回零不能替代它。

## 资源和取消

按 Ctrl+C 取消；总时限八分钟，每阶段九十秒，最多三十二个子进程、每进程最多六十四参数、每输出管道最多四 MiB 字符。内存采样最多九百轮且共用阶段与总时限，等待间隔 100ms，每十秒输出阶段进度。进程句柄、PID、创建时间、父进程和完整命令参数写入 `.runs/media-baseline/<本次ID>/`；这些本地审计记录含路径，不得提交。

所有结束路径只终止同一个 Process 对象和创建时间确定的本次进程树，等待退出上限十秒，再排空/取消输出读取（上限五秒）。临时样本位于系统临时目录下唯一的 `mps-media-baseline-<GUID>`；删除前验证完整路径、前缀和 GUID，拒绝目录重解析点。成功报告确认所有子进程已退出和临时媒体已删除。本地审计日志与用户指定的聚合报告作为验证证据保留；不会启动常驻服务。

本工具不代替真实桌面预览、录屏、麦克风/系统音频录制、多轨操作、五分钟成片、Windows 10、硬件编码或两套代表性机器验收。具体观测与候选回归阈值见 [2026-09-29 测量记录](../../docs/milestone-a/media-baseline-2026-09-29.md)。
