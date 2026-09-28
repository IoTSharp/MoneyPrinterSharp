# MPS 合成演示工程

全部界面、录屏、音调和主持人替身由本仓库 `tools/DemoAssets` 与 FFmpeg 在本地生成，按仓库 MIT 许可使用。画面显著标为 DEMO ONLY，不代表真实软件，也不含用户业务数据。

`tone.wav` 是音调，不是中文或英文配音；`captions.srt` 仅用于字幕/时码回归，不满足成片旁白验收。`presenter-placeholder.mp4` 是几何形状，`lip_synced=false`，不能宣称口型同步。四秒素材只用于极短媒体回归，不在 V1 15 秒交付范围内。

重新生成：`dotnet run --project tools/DemoAssets/DemoAssets.csproj -- --output NEW_EMPTY_DIR`。
