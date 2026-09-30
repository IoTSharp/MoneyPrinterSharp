# 时间轴坐标契约

`TimelineCoordinates.cs` 为新项目的预览、编辑与渲染提供共同的精确时间基础；当前旧版 `manifest.json` 和 CLI 媒体命令尚未改用它。时间坐标是约分后的有理数秒 `RationalTime`（`numerator/denominator`），不通过浮点数逐帧累加。`SourceTime` 与 `ProjectTime` 是不同类型，调用方必须显式映射，不能把源入点当作项目位置。

| 坐标 | 产生方式 | 边界 |
| --- | --- | --- |
| 源时间 | 音频采样序号/采样率，或 `(PTS - first_pts) * time_base_num / time_base_den` | `first_pts` 来自已索引素材；允许原始负 PTS，归零后不得为负 |
| 项目时间 | 片段映射、项目音频采样序号或项目帧序号 | 从项目零点起；片段变速后按绝对源时间计算 |
| 项目帧 | `project_time * fps_num / fps_den` | 仅在输出边界按 `Floor`、`Nearest`（半值向上）或 `Ceiling` 舍入 |
| 音频采样 | `time * sample_rate` | 仅在输出边界按明确策略舍入，采样率为 1-384000 Hz |

单个时间坐标最长 12 小时，避免合理长源素材被 5 分钟成片上限误拒；成片 15-300 秒限制由后续项目/导出验证处理。帧率为 1-240 fps 的有理数，含 24、25、30、60 与 30000/1001；速度为源秒数/项目秒数，允许 0.01-100。超时长、负坐标、无效时间基、不可精确存入 Int64 的分数均抛错，不截断或回退浮点数。

片段保存 `source_in`、`source_out`、`timeline_start` 与正数 `speed_num/speed_den`：

```text
timeline(source) = timeline_start + (source - source_in) / speed
timeline_end = timeline(source_out)
```

`source_out` 是裁切右边界；映射函数允许等于右边界以计算片段终点，实际帧选择须采用 `[source_in, source_out)`，避免相邻片段重复末帧。预览定位通过同一映射的逆函数得到源时间。VFR 素材每帧读取自己的绝对呈现时间戳及时间基，以素材索引记录的 `first_pts` 归零后调用 `MapPts`；索引应按呈现时间处理帧，不假定帧间隔固定。不得将各帧间隔先取整为项目帧再累加。时间基或 PTS 缺失时应标记未知并停止精确定位，不能猜测标称帧率。

整数项目帧率提供非丢帧 `HH:MM:SS:FF` 显示时间码，显示帧号取决于调用方指定的舍入方式。分数帧率需要明确选择 SMPTE 丢帧或非丢帧显示规则，当前 `FormatTimecode` 明确拒绝，底层时间和帧转换仍可精确使用。此处不把 30000/1001 误写成 30 fps 的墙钟时间码。

新项目 JSON 后续应把时间写成整数 `{ "num": 1, "den": 24 }` 形式，并分别存储 `fps_num/fps_den` 与 `speed_num/speed_den`；这一序列化及时间线轨道接入属于后续项目模型工作，当前类本身不修改根文件格式。

`TimelineCoordinatesTests.Run()` 已接入常规离线回归，纯本地、20 秒截止且可取消：验证 24/25/30/60 fps 边界、采样与帧半值舍入、裁切及变速可逆性、负起始 PTS、一万二千个不等间隔 VFR 时间戳的逐项映射与绝对时间一致，以及逐间隔取整造成的显著漂移。
